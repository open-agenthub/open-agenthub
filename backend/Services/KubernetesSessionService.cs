using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentHub.Api.Agents;
using AgentHub.Api.Controllers;
using AgentHub.Api.Library;
using AgentHub.Api.Files;
using AgentHub.Api.Models;
using AgentHub.Api.Browser;
using AgentHub.Api.Persistence;
using AgentHub.Api.Storage;
using k8s;
using k8s.Models;

namespace AgentHub.Api.Services;

/// <summary>
/// Implements sessions as native Kubernetes objects and keeps a registry in Postgres.
///   - Interactive/Autonomous => one pod per session
///   - Scheduled              => a CronJob
/// Resume works via Claude Code's own session state (.tgz in S3), no PVC.
/// Security: no root, no capabilities, read-only rootfs, dedicated namespace,
/// the agent pod gets no S3 creds (only presigned URLs) and no K8s token.
/// </summary>
public sealed class KubernetesSessionService : ISessionService, IProviderLoginRepository
{
    private readonly Kubernetes _k8s;
    private readonly ISessionExpiryLock? _runtimeUpdateLock;
    private readonly ISessionStore _store;
    private readonly IProjectStore _projects;
    private readonly IArtifactStore _artifacts;
    private readonly IBrowserService _browsers;
    private readonly IGitAuthService _gitAuth;
    private readonly Usage.UsageLimitService _usageLimits;
    private readonly IAllowedAgentsProvider _allowedAgents;
    private readonly ILibraryAccess _library;
    private readonly IMcpServerStore _mcpServers;
    private readonly IEphemeralApiMcpStore _ephemeralApiMcps;
    private readonly IMcpGatewayTokenService _mcpGatewayTokens;
    private readonly ISessionFileCleanup? _fileCleanup;
    private readonly Network.INetworkSessionCleanup? _networkCleanup;
    private readonly IAgentCredentialPusher? _credentialPusher;
    private readonly Containers.ISessionContainerCleanup? _containerCleanup;
    private readonly ISecretRequestStore? _secretRequests;
    private readonly ILogger<KubernetesSessionService> _log;
    private readonly AgentHubOptions _opts;
    private readonly string _callbackBaseUrl;
    private readonly string _mcpGatewayBaseUrl;
    private readonly bool _s3Insecure;
    private readonly bool _browserEnabled;
    private readonly bool _spawnMcpEnabled;
    private readonly bool _networkMcpEnabled;
    private readonly bool _containersEnabled;
    private readonly int _maxRunningSessionsPerOwner;
    private readonly string? _frontendOrigin;

    private const string OwnerLabel = "agenthub.dev/owner";
    private const string SessionLabel = "agenthub.dev/session";
    private const string ComponentLabel = "agenthub.dev/component";
    private static readonly TimeSpan PresignTtl = TimeSpan.FromHours(12);

    public KubernetesSessionService(IConfiguration cfg, ISessionStore store, IProjectStore projects,
        IArtifactStore artifacts, IBrowserService browsers, IGitAuthService gitAuth,
        Usage.UsageLimitService usageLimits, IAllowedAgentsProvider allowedAgents,
        ILibraryAccess library, IMcpServerStore mcpServers, IEphemeralApiMcpStore ephemeralApiMcps,
        IMcpGatewayTokenService mcpGatewayTokens, ILogger<KubernetesSessionService> log,
        ISessionFileCleanup? fileCleanup = null, Network.INetworkSessionCleanup? networkCleanup = null,
        IAgentCredentialPusher? credentialPusher = null,
        Containers.ISessionContainerCleanup? containerCleanup = null,
        ISessionExpiryLock? runtimeUpdateLock = null,
        ISecretRequestStore? secretRequests = null)
    {
        _secretRequests = secretRequests;
        _log = log;
        _runtimeUpdateLock = runtimeUpdateLock;
        _containerCleanup = containerCleanup;
        _credentialPusher = credentialPusher;
        _store = store;
        _projects = projects;
        _artifacts = artifacts;
        _browsers = browsers;
        _gitAuth = gitAuth;
        _usageLimits = usageLimits;
        _allowedAgents = allowedAgents;
        _library = library;
        _mcpServers = mcpServers;
        _ephemeralApiMcps = ephemeralApiMcps;
        _mcpGatewayTokens = mcpGatewayTokens;
        _fileCleanup = fileCleanup;
        _networkCleanup = networkCleanup;
        _opts = cfg.GetSection("AgentHub").Get<AgentHubOptions>() ?? new AgentHubOptions();
        _callbackBaseUrl = cfg["AgentHub:CallbackBaseUrl"]
            ?? "http://agenthub-backend.agenthub.svc.cluster.local";
        // What agent pods use to reach the in-process OpenAPI MCP gateway.
        // Empty falls back to CallbackBaseUrl (same in-cluster backend service).
        var configuredGateway = cfg["McpGateway:BaseUrl"];
        _mcpGatewayBaseUrl = string.IsNullOrWhiteSpace(configuredGateway)
            ? _callbackBaseUrl.TrimEnd('/')
            : configuredGateway.TrimEnd('/');
        _s3Insecure = cfg.GetValue("S3:InsecureTls", false);
        _browserEnabled = cfg.GetValue("Browser:Enabled", true);
        _spawnMcpEnabled = cfg.GetValue("AgentHub:SpawnMcpEnabled", true);
        _networkMcpEnabled = cfg.GetValue("Network:Enabled", true);
        _containersEnabled = cfg.GetValue("SessionContainers:Enabled", false);
        _maxRunningSessionsPerOwner = SessionSoftLimit.NormalizeMax(
            cfg.GetValue("AgentHub:MaxRunningSessionsPerOwner", SessionSoftLimit.DefaultMax));
        // Origin of the session link returned to an API or MCP caller. See SessionUrl for why this
        // and not the request host.
        _frontendOrigin = cfg["FrontendOrigin"];

        var config = KubernetesClientConfiguration.IsInCluster()
            ? KubernetesClientConfiguration.InClusterConfig()
            : KubernetesClientConfiguration.BuildConfigFromConfigFile();
        _k8s = new Kubernetes(config);
    }

    // ---------------------------------------------------------------- Credentials

    public static string CredentialKey(string propertyName) => CredentialSecretFactory.CredentialKey(propertyName);


    public async Task StoreCredentialsAsync(string owner, UserCredentials c, CancellationToken ct = default)
    {
        var name = CredsSecretName(owner);
        var existing = (await ReadSecretOrNullAsync(name, ct))?.Data;
        var secret = CredentialSecretFactory.CreateGeneralSecret(name, _opts.Namespace, Sanitize(owner), existing, c);
        await UpsertSecretAsync(secret, ct);
        _log.LogInformation("Stored credentials for {Owner} ({Keys} keys)", owner, secret.Data.Count);
    }

    public async Task<GitPatInfo> UpsertGitPatAsync(string owner, UpsertGitPatRequest request, CancellationToken ct = default)
    {
        var name = CredsSecretName(owner);
        var existing = (await ReadSecretOrNullAsync(name, ct))?.Data;
        var (secret, info) = CredentialSecretFactory.UpsertGitPat(name, _opts.Namespace, Sanitize(owner), existing, request);
        await UpsertSecretAsync(secret, ct);
        _log.LogInformation("Stored {Kind} token for {Host} for {Owner}", info.Kind, info.Host, owner);
        return info;
    }

    public async Task DeleteGitPatAsync(string owner, string id, CancellationToken ct = default)
    {
        var name = CredsSecretName(owner);
        var existing = (await ReadSecretOrNullAsync(name, ct))?.Data;
        // Nothing stored at all: writing an empty secret would only create one for a user who has
        // never saved a credential.
        if (existing is null) return;
        await UpsertSecretAsync(CredentialSecretFactory.RemoveGitPat(name, _opts.Namespace, Sanitize(owner), existing, id), ct);
        // The id comes straight from the route and is never validated against a format, so it
        // stays out of the log line: a crafted value could forge extra log entries.
        _log.LogInformation("Removed a git token for {Owner}", owner);
    }

    // ---------------------------------------------------------------- Session secrets

    public async Task<IReadOnlyList<SessionSecretInfo>> ListSessionSecretsAsync(string owner, CancellationToken ct = default)
        => SessionSecretStore.Read((await ReadSecretOrNullAsync(UserSecretsName(owner), ct))?.Data)
            .Select(SessionSecretStore.Info).ToList();

    public async Task<SessionSecretInfo> UpsertSessionSecretAsync(string owner, UpsertSessionSecretRequest request,
        CancellationToken ct = default)
    {
        var entry = await ChangeUserSecretsAsync(owner,
            data => SessionSecretStore.Upsert(data, request, DateTime.UtcNow), ct);
        // The id and nothing else: the name is the owner's text and the value never goes near a log.
        _log.LogInformation("Stored secret {Id} for {Owner}", entry.Id, owner);
        await TrySyncSessionSecretsAsync(owner, ct);
        return SessionSecretStore.Info(entry);
    }

    public async Task DeleteSessionSecretAsync(string owner, string id, CancellationToken ct = default)
    {
        if (await ReadSecretOrNullAsync(UserSecretsName(owner), ct) is null) return;
        var removed = await ChangeUserSecretsAsync(owner, data => SessionSecretStore.Remove(data, id), ct);
        if (!removed) return;
        _log.LogInformation("Removed a secret for {Owner}", owner);
        await TrySyncSessionSecretsAsync(owner, ct);
    }

    public async Task<IReadOnlyList<string>> ResolveSessionSecretIdsAsync(string owner, string? projectId,
        IReadOnlyList<string>? requested, CancellationToken ct = default)
    {
        var stored = SessionSecretStore.Read((await ReadSecretOrNullAsync(UserSecretsName(owner), ct))?.Data);
        var selected = requested is null || requested.Contains(SessionSecretSelection.FollowProject) ? null : requested;
        return SessionSecretSelection.Effective(selected, projectId, await ProjectSecretIdsAsync(owner, projectId, ct), stored);
    }

    public async Task SyncSessionSecretsAsync(string owner, CancellationToken ct = default)
    {
        // Every session that was ever spawned has a file copy, so the copies are the list of
        // sessions to bring up to date; a session that never ran has nothing to update.
        var copies = await _k8s.CoreV1.ListNamespacedSecretAsync(_opts.Namespace,
            labelSelector: $"{OwnerLabel}={Sanitize(owner)},{ComponentLabel}={SessionSecretDeliveryFactory.FilesComponent}",
            cancellationToken: ct);
        if (copies.Items.Count == 0) return;
        var store = (await ReadSecretOrNullAsync(UserSecretsName(owner), ct))?.Data;
        var sessionIds = copies.Items
            .Select(copy => copy.Metadata.Labels is { } labels && labels.TryGetValue(SessionLabel, out var id) ? id : null)
            .OfType<string>();
        foreach (var sessionId in sessionIds)
        {
            if (await _store.GetAsync(owner, sessionId, ct) is { } rec)
            {
                await WriteSessionSecretsAsync(owner, rec, store, ct);
                continue;
            }
            // The session is gone but its copies are not (a delete that failed half-way): they hold
            // values nobody can receive any more, and no later session delete would reach them.
            await DeleteSecretIfPresentAsync(SessionSecretDeliveryFactory.FilesSecretName(sessionId), ct);
            await DeleteSecretIfPresentAsync(SessionSecretDeliveryFactory.EnvSecretName(sessionId), ct);
        }
    }

    /// <summary>Masks with what the session's own copies hold — exactly the values the pod can
    /// see, rather than the whole store, whose other values the session never had.</summary>
    public async Task<string> MaskSessionSecretsAsync(string sessionId, string text, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var files = (await ReadSecretOrNullAsync(SessionSecretDeliveryFactory.FilesSecretName(sessionId), ct))?.Data;
        var env = (await ReadSecretOrNullAsync(SessionSecretDeliveryFactory.EnvSecretName(sessionId), ct))?.Data;
        if (files is not { Count: > 0 } && env is not { Count: > 0 }) return text;
        return SecretMask.Apply(text, (files ?? new Dictionary<string, byte[]>()).Concat(env ?? new Dictionary<string, byte[]>()));
    }

    /// <summary>
    /// The store write is what the owner asked for and has happened; a session whose copy could
    /// not be rewritten gets it at its next start at the latest. Failing the request over it would
    /// tell the owner their secret was not saved when it was.
    /// </summary>
    private async Task TrySyncSessionSecretsAsync(string owner, CancellationToken ct)
    {
        try { await SyncSessionSecretsAsync(owner, ct); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.LogWarning(e, "Could not bring the running sessions of {Owner} up to date with their secrets", owner);
        }
    }

    /// <summary>
    /// Writes the session's two secret copies from the current store and selections and returns
    /// the names of the variables, which the pod spec references. Called at every spawn and on
    /// every change: the kubelet then carries a file change into the running pod by itself.
    /// </summary>
    private async Task<IReadOnlyList<string>> WriteSessionSecretsAsync(string owner, SessionRecord rec,
        IDictionary<string, byte[]>? store, CancellationToken ct)
    {
        var projectIds = await ProjectSecretIdsAsync(owner, rec.ProjectId, ct);
        var ids = SessionSecretSelection.Effective(SessionSecretSelection.Parse(rec.SecretIdsJson), rec.ProjectId,
            projectIds, SessionSecretStore.Read(store), SessionSecretSelection.Parse(rec.SecretGrantIdsJson));
        var delivery = SessionSecretDeliveryFactory.Build(rec.Id, _opts.Namespace,
            new Dictionary<string, string> { [OwnerLabel] = Sanitize(owner), [SessionLabel] = rec.Id }, store, ids);
        await UpsertSecretAsync(delivery.Files, ct);
        await UpsertSecretAsync(delivery.Env, ct);
        return delivery.EnvNames;
    }

    public async Task<IReadOnlyList<string>> SessionSecretIdsAsync(string owner, SessionRecord session,
        CancellationToken ct = default)
    {
        var stored = SessionSecretStore.Read((await ReadSecretOrNullAsync(UserSecretsName(owner), ct))?.Data);
        return SessionSecretSelection.Effective(SessionSecretSelection.Parse(session.SecretIdsJson), session.ProjectId,
            await ProjectSecretIdsAsync(owner, session.ProjectId, ct), stored,
            SessionSecretSelection.Parse(session.SecretGrantIdsJson));
    }

    public async Task<SessionSecretGrant> GrantSessionSecretAsync(string owner, string sessionId, string secretId,
        CancellationToken ct = default)
    {
        var rec = await _store.GetAsync(owner, sessionId, ct) ?? throw new KeyNotFoundException($"Session {sessionId} not found.");
        var store = (await ReadSecretOrNullAsync(UserSecretsName(owner), ct))?.Data;
        var entry = SessionSecretStore.Read(store).FirstOrDefault(e => e.Id == secretId)
            ?? throw new ArgumentException($"No stored secret with id '{secretId}'.");
        // The limit a restricted token or a spawning parent set holds for what the agent asks for
        // later, too; otherwise asking would be the way around it.
        if (SessionSecretSelection.Parse(rec.SecretCeilingJson) is { } ceiling && !ceiling.Contains(secretId))
            throw new CredentialScopeException(CredentialScopeException.SecretNotAllowed);
        var grants = SessionSecretSelection.Parse(rec.SecretGrantIdsJson)?.ToList() ?? [];
        if (!grants.Contains(secretId))
        {
            grants.Add(secretId);
            rec.SecretGrantIdsJson = SessionSecretSelection.Serialize(grants);
            await _store.UpsertAsync(rec, ct);
        }
        await RefreshSessionSecretsAsync(owner, rec, ct);
        // The stored id, not the one from the request body, which a caller could fill with log lines.
        _log.LogInformation("Granted secret {SecretId} to session {Id}", entry.Id, rec.Id);
        // A file arrives in the running pod on its own; a variable only with a container that
        // starts after this, unless the running one already references it.
        var restart = false;
        if (entry.Delivery == Models.SessionSecretDelivery.Env && await TryReadPodAsync($"session-{rec.Id}", ct) is { } pod)
            restart = pod.Spec?.Containers?.FirstOrDefault()?.Env?.Any(e => e.Name == entry.Name) != true;
        return new SessionSecretGrant(entry.Id, entry.Name, entry.Delivery, restart);
    }

    /// <summary>Rewrites the copies of one session that has been spawned before; a session that
    /// never ran gets them at its first start.</summary>
    private async Task RefreshSessionSecretsAsync(string owner, SessionRecord rec, CancellationToken ct)
    {
        if (await ReadSecretOrNullAsync(SessionSecretDeliveryFactory.FilesSecretName(rec.Id), ct) is null) return;
        await WriteSessionSecretsAsync(owner, rec,
            (await ReadSecretOrNullAsync(UserSecretsName(owner), ct))?.Data, ct);
    }

    private async Task<IReadOnlyList<string>> ProjectSecretIdsAsync(string owner, string? projectId, CancellationToken ct)
        => projectId is null
            ? Array.Empty<string>()
            : (await _projects.GetAsync(owner, projectId, ct))?.SecretIds ?? Array.Empty<string>();

    /// <summary>A requested selection checked against the store and the project; null passes as
    /// "follow the project" without reading anything.</summary>
    private async Task<IReadOnlyList<string>?> ValidateSecretIdsAsync(string owner, string? projectId,
        IReadOnlyList<string>? requested, CancellationToken ct)
    {
        if (requested is null) return null;
        var stored = requested.Count == 0
            ? Array.Empty<SessionSecretStore.Entry>()
            : SessionSecretStore.Read((await ReadSecretOrNullAsync(UserSecretsName(owner), ct))?.Data);
        var selected = SessionSecretSelection.Normalize(requested, stored);
        SessionSecretSelection.EnsureWithinProject(selected, projectId, await ProjectSecretIdsAsync(owner, projectId, ct));
        return selected;
    }

    /// <summary>
    /// Read, change, write with the version that was read, retried on a conflict. The secret
    /// request flow writes here while the owner may be editing the same store in another tab; a
    /// plain replace would let the later of two writes silently drop the other's secret.
    /// </summary>
    /// <summary>Five attempts without a pause were exhausted by eight writers at once against a real
    /// API server. Beyond this the request fails rather than retrying without bound.</summary>
    private const int MaxStoreWriteAttempts = 16;

    private async Task<T> ChangeUserSecretsAsync<T>(string owner, Func<Dictionary<string, byte[]>, T> change, CancellationToken ct)
    {
        var name = UserSecretsName(owner);
        for (var attempt = 1; ; attempt++)
        {
            var existing = await ReadSecretOrNullAsync(name, ct);
            var data = existing?.Data is null ? new Dictionary<string, byte[]>() : new Dictionary<string, byte[]>(existing.Data);
            var result = change(data);
            var secret = new V1Secret
            {
                Metadata = new V1ObjectMeta
                {
                    Name = name, NamespaceProperty = _opts.Namespace,
                    Labels = new Dictionary<string, string> { [OwnerLabel] = Sanitize(owner) },
                    ResourceVersion = existing?.Metadata.ResourceVersion
                },
                Type = "Opaque",
                Data = data
            };
            try
            {
                if (existing is null) await _k8s.CoreV1.CreateNamespacedSecretAsync(secret, _opts.Namespace, cancellationToken: ct);
                else await _k8s.CoreV1.ReplaceNamespacedSecretAsync(secret, name, _opts.Namespace, cancellationToken: ct);
                return result;
            }
            catch (k8s.Autorest.HttpOperationException e)
                when (e.Response.StatusCode == System.Net.HttpStatusCode.Conflict && attempt < MaxStoreWriteAttempts)
            {
                // Jittered, so writers that collided do not collide again on the next round.
                await Task.Delay(Random.Shared.Next(5, 20 * attempt), ct);
            }
        }
    }

    /// <summary>Which credential fields have a stored value. Values are never returned.</summary>
    public async Task<CredentialStatus> GetCredentialStatusAsync(string owner, CancellationToken ct = default)
    {
        var data = (await ReadSecretOrNullAsync(CredsSecretName(owner), ct))?.Data ?? new Dictionary<string, byte[]>();
        var claude = (await ReadSecretOrNullAsync(ProviderSecretName(owner, AgentKind.Claude), ct))?.Data;
        var codex = (await ReadSecretOrNullAsync(ProviderSecretName(owner, AgentKind.Codex), ct))?.Data;
        var cursor = (await ReadSecretOrNullAsync(ProviderSecretName(owner, AgentKind.Cursor), ct))?.Data;
        var openclaw = (await ReadSecretOrNullAsync(ProviderSecretName(owner, AgentKind.OpenClaw), ct))?.Data;
        var opencode = (await ReadSecretOrNullAsync(ProviderSecretName(owner, AgentKind.OpenCode), ct))?.Data;
        return CredentialSecretFactory.CredentialStatus(data, claude, codex, cursor, openclaw, opencode);
    }

    /// <summary>
    /// Stores provider CLI subscription credentials in a dedicated secret.
    /// Separate secret so StoreCredentialsAsync (which fully replaces its secret) does not overwrite it.
    /// </summary>
    public Task StoreProviderCredentialsAsync(string owner, AgentKind agent, string json, CancellationToken ct = default)
        => StoreProviderLoginAsync(owner, agent, json, identity: null, mountedCredentialId: null, ct);

    public async Task<string?> StoreProviderLoginAsync(string owner, AgentKind agent, string json,
        ProviderAccountIdentity? identity, string? mountedCredentialId, CancellationToken ct = default)
    {
        if (!ProviderCredentialValidator.Validate(agent, json))
            throw new ArgumentException("Invalid provider credential document.", nameof(json));
        var attached = await MutateProviderAccountsAsync(owner, agent,
            set => ProviderAccountSecret.Attach(set, Encoding.UTF8.GetBytes(json), identity, mountedCredentialId, agent: agent), ct);
        _log.LogInformation("Saved {Agent} login for {Owner} into account {Account} ({Outcome})",
            agent, owner, attached.AccountId,
            attached.Created ? "new" : attached.Stale ? "older than the stored one, kept the stored one" : "updated");
        return attached.AccountId;
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<ProviderAccountInfo>>> ListProviderAccountsAsync(
        string owner, CancellationToken ct = default)
    {
        var result = new Dictionary<string, IReadOnlyList<ProviderAccountInfo>>();
        var now = DateTime.UtcNow;
        foreach (var agent in new[] { AgentKind.Claude, AgentKind.Codex, AgentKind.Cursor, AgentKind.OpenClaw, AgentKind.OpenCode })
        {
            var set = await ReadProviderAccountsAsync(owner, agent, ct);
            result[agent.ToString()] = set.Accounts.Select(a => ProviderAccountInfo.From(a, now)).ToList();
        }
        return result;
    }

    public async Task<ProviderAccountInfo?> UpdateProviderAccountAsync(string owner, AgentKind agent, string id,
        UpdateProviderAccountRequest req, CancellationToken ct = default)
    {
        var label = req.Label is null ? null : ProviderAccountSecret.NormalizeLabel(req.Label);
        var account = await MutateProviderAccountsAsync(owner, agent, set =>
        {
            var found = set.Find(id);
            if (found is null) return null;
            if (label is not null) { found.Label = label; set.Dirty = true; }
            if (req.IsDefault == true) ProviderAccountSecret.MakeDefault(set, id);
            if (req.ClearExhausted == true) ProviderAccountSecret.ClearExhausted(set, id);
            return found;
        }, ct);
        return account is null ? null : ProviderAccountInfo.From(account);
    }

    public async Task<ProviderAccountInfo?> MarkProviderAccountExhaustedAsync(string owner, AgentKind agent, string id,
        DateTime until, string? reason, CancellationToken ct = default)
    {
        var account = await MutateProviderAccountsAsync(owner, agent,
            set => ProviderAccountSecret.MarkExhausted(set, id, until, reason) ? set.Find(id) : null, ct);
        if (account is null) return null;
        _log.LogInformation("Marked {Agent} account {Account} of {Owner} as at its usage limit until {Until}",
            agent, id, owner, until);
        return ProviderAccountInfo.From(account);
    }

    public async Task DeleteProviderAccountAsync(string owner, AgentKind agent, string id, CancellationToken ct = default)
    {
        if (!await MutateProviderAccountsAsync(owner, agent, set => ProviderAccountSecret.Remove(set, id), ct)) return;
        _log.LogInformation("Deleted {Agent} account for {Owner}", agent, owner);
    }

    /// <summary>
    /// Reads the provider secret as accounts. Any read that had to normalise the layout — the
    /// lazy migration of a single-file secret, a reconciled index — is written back straight
    /// away, so a pod spec built a moment later projects a key that exists.
    /// </summary>
    private async Task<ProviderAccountSet> ReadProviderAccountsAsync(string owner, AgentKind agent, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var secret = await ReadSecretOrNullAsync(ProviderSecretName(owner, agent), ct);
            var set = ProviderAccountSecret.Read(secret?.Data, agent);
            set.ResourceVersion = secret?.Metadata?.ResourceVersion;
            if (!set.Dirty || secret?.Data is null) return set;
            try
            {
                await WriteProviderAccountsAsync(owner, agent, set, ct);
                _log.LogInformation("Normalised the {Agent} login secret of {Owner} to the account layout", agent, owner);
                return set;
            }
            catch (ProviderSecretConflictException) when (attempt < MaxSecretWriteAttempts)
            {
                // Most likely the same normalisation from another request; the next read sees it.
                _log.LogDebug("The {Agent} login secret of {Owner} changed while it was normalised; reading again", agent, owner);
            }
        }
    }

    /// <summary>
    /// Read, change, write — again from the read if the secret changed in between. Several
    /// writers share one secret: every pod of the owner uploading its login, the background
    /// refresher, the user editing a label, a usage-limit mark. A plain replace lets the last of
    /// two concurrent writers drop the other's change without a trace, and when the dropped change
    /// is a rotated token, the account is left holding a refresh token that was already spent.
    /// </summary>
    private async Task<T> MutateProviderAccountsAsync<T>(string owner, AgentKind agent,
        Func<ProviderAccountSet, T> change, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var set = await ReadProviderAccountsAsync(owner, agent, ct);
            var result = change(set);
            if (!set.Dirty) return result;
            try
            {
                await WriteProviderAccountsAsync(owner, agent, set, ct);
                return result;
            }
            catch (ProviderSecretConflictException) when (attempt < MaxSecretWriteAttempts)
            {
                _log.LogDebug("The {Agent} login secret of {Owner} changed concurrently; applying the change again", agent, owner);
            }
        }
    }

    private const int MaxSecretWriteAttempts = 5;

    private async Task WriteProviderAccountsAsync(string owner, AgentKind agent, ProviderAccountSet set, CancellationToken ct)
    {
        if (set.Accounts.Count == 0)
        {
            await DeleteProviderCredentialsAsync(owner, agent, ct);
            return;
        }
        await WriteProviderSecretAsync(ProviderSecretName(owner, agent), Sanitize(owner), agent, set, ct);
    }

    /// <summary>
    /// Writes the set only if the secret is still the version it was read at: a create when it was
    /// read as absent, a replace carrying the read's resourceVersion otherwise. The apiserver
    /// answers a mismatch with 409, surfaced as <see cref="ProviderSecretConflictException"/>.
    /// </summary>
    private async Task WriteProviderSecretAsync(string name, string ownerLabel, AgentKind agent, ProviderAccountSet set,
        CancellationToken ct)
    {
        var secret = CredentialSecretFactory.ProviderSecret(name, _opts.Namespace, ownerLabel, ProviderAccountSecret.Write(set, agent));
        V1Secret written;
        try
        {
            if (set.ResourceVersion is null)
            {
                written = await _k8s.CoreV1.CreateNamespacedSecretAsync(secret, _opts.Namespace, cancellationToken: ct);
            }
            else
            {
                secret.Metadata.ResourceVersion = set.ResourceVersion;
                written = await _k8s.CoreV1.ReplaceNamespacedSecretAsync(secret, name, _opts.Namespace, cancellationToken: ct);
            }
        }
        catch (k8s.Autorest.HttpOperationException e) when (e.Response.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            throw new ProviderSecretConflictException();
        }
        set.ResourceVersion = written.Metadata?.ResourceVersion;
        set.Dirty = false;
    }

    // ---------------------------------------------------------------- IProviderLoginRepository

    private static string ProviderSecretPrefix(AgentKind agent) => $"{agent.ToString().ToLowerInvariant()}-";

    public async Task<IReadOnlyList<string>> ListSecretNamesAsync(AgentKind agent, CancellationToken ct)
    {
        var prefix = ProviderSecretPrefix(agent);
        var secrets = await _k8s.CoreV1.ListNamespacedSecretAsync(_opts.Namespace, labelSelector: OwnerLabel,
            resourceVersion: "0", cancellationToken: ct);
        return secrets.Items
            .Select(s => s.Metadata?.Name)
            .Where(n => n is not null && n.StartsWith(prefix + "u-", StringComparison.Ordinal))
            .Select(n => n!)
            .ToList();
    }

    public async Task<ProviderLoginSnapshot?> ReadAsync(string secretName, AgentKind agent, CancellationToken ct)
    {
        if (!secretName.StartsWith(ProviderSecretPrefix(agent), StringComparison.Ordinal)) return null;
        var secret = await ReadSecretOrNullAsync(secretName, ct);
        if (secret?.Data is null) return null;
        var set = ProviderAccountSecret.Read(secret.Data, agent);
        set.ResourceVersion = secret.Metadata?.ResourceVersion;
        return new ProviderLoginSnapshot(secretName, set);
    }

    public async Task<bool> TryWriteAsync(ProviderLoginSnapshot snapshot, AgentKind agent, CancellationToken ct)
    {
        var prefix = ProviderSecretPrefix(agent);
        if (!snapshot.SecretName.StartsWith(prefix, StringComparison.Ordinal) || snapshot.Accounts.Accounts.Count == 0
            || snapshot.Accounts.ResourceVersion is null)
            return false;
        try
        {
            await WriteProviderSecretAsync(snapshot.SecretName, snapshot.SecretName[prefix.Length..], agent, snapshot.Accounts, ct);
            return true;
        }
        catch (ProviderSecretConflictException)
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<ProviderLoginSession>> ListRunningSessionsAsync(string secretName, AgentKind agent,
        string accountId, CancellationToken ct)
    {
        var prefix = ProviderSecretPrefix(agent);
        if (!secretName.StartsWith(prefix, StringComparison.Ordinal)) return Array.Empty<ProviderLoginSession>();
        // The secret is named after the owner label, which is all that is needed to find the
        // owner's pods; the owner's name itself is not recoverable from it and not needed.
        var pods = await _k8s.CoreV1.ListNamespacedPodAsync(_opts.Namespace,
            labelSelector: $"{OwnerLabel}={secretName[prefix.Length..]},{ComponentLabel}=agent",
            resourceVersion: "0", cancellationToken: ct);
        var running = pods.Items
            .Where(pod => pod.Status?.Phase == "Running" && !string.IsNullOrEmpty(pod.Status.PodIP)
                && pod.Metadata?.Labels?.ContainsKey(SessionLabel) == true)
            .Select(pod => (Ip: pod.Status.PodIP, SessionId: pod.Metadata.Labels[SessionLabel]));
        var sessions = new List<ProviderLoginSession>();
        foreach (var (ip, sessionId) in running)
        {
            var record = await _store.GetByIdAsync(sessionId, ct);
            // The account a session is on: the one it is pinned to, else the one resolved at start.
            var onAccount = record is not null && record.Agent == agent && record.AuthMode != AgentAuthMode.ApiKey
                && (record.CredentialId ?? record.ResolvedCredentialId) == accountId;
            if (onAccount) sessions.Add(new ProviderLoginSession(record!.Id, ip, record.CallbackToken));
        }
        return sessions;
    }

    /// <summary>Checks that a requested account exists for the agent; null passes through as "default".</summary>
    private async Task<string?> ValidateCredentialIdAsync(string owner, AgentKind agent, AgentAuthMode authMode,
        string? credentialId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(credentialId)) return null;
        if (authMode == AgentAuthMode.ApiKey)
            throw new ArgumentException("A provider account only applies to Subscription sessions.");
        if (!ProviderAccountSecret.IsValidId(credentialId))
            throw new ArgumentException("Invalid provider account id.");
        var set = await ReadProviderAccountsAsync(owner, agent, ct);
        if (set.Find(credentialId) is null)
            throw new ArgumentException($"No stored {agent} login with id '{credentialId}'.");
        return credentialId;
    }

    /// <summary>
    /// Checks a requested PAT selection against what is stored; null and the wildcard pass
    /// through as "all". The secret is read only when there is something to check, so a request
    /// that says nothing about PATs costs no extra round trip.
    /// </summary>
    private async Task<IReadOnlyList<string>?> ValidateGitPatIdsAsync(string owner, IReadOnlyList<string>? requested,
        CancellationToken ct)
    {
        if (requested is null) return null;
        if (requested.Count == 0) return Array.Empty<string>();
        var stored = GitPatStore.Read((await ReadSecretOrNullAsync(CredsSecretName(owner), ct))?.Data);
        return GitPatSelection.Normalize(requested, stored);
    }

    public Task<SessionInfo> SwitchSessionCredentialAsync(string owner, string id, string credentialId,
        CancellationToken ct = default)
        => SwitchSessionCredentialAsync(owner, id, credentialId, reason: null, ct);

    /// <summary>
    /// Stores a new git token selection and, for a running pod, installs it there at once
    /// (docs/git-credentials.md, "Switching the token of a running session"). Unlike a provider
    /// account this needs no restart — git reads its store on every command — so the only reason to
    /// fall back to "next start" is a pod that cannot take it: not running, or an image from before
    /// the route. The selection is saved first either way, so the next start uses it regardless.
    /// </summary>
    public async Task<GitPatSwitchResult> SwitchSessionGitPatsAsync(string owner, string id,
        IReadOnlyList<string>? gitPatIds, CancellationToken ct = default)
    {
        var rec = await _store.GetAsync(owner, id, ct)
            ?? throw new KeyNotFoundException($"Session {id} not found.");
        rec.GitPatIdsJson = GitPatSelection.Serialize(await ValidateGitPatIdsAsync(owner, gitPatIds, ct));
        await _store.UpsertAsync(rec, ct);

        var pod = await TryReadPodAsync($"session-{id}", ct);
        var appliedNow = false;
        if (pod?.Status?.Phase == "Running" && pod.Status.PodIP is { Length: > 0 } podIp && _credentialPusher is not null)
        {
            var git = await BuildGitCredentialsAsync(owner, rec, ParseRepos(rec), ct)
                ?? new SessionGitCredentials("", Array.Empty<GitAuthor>());
            // The mounted copy follows too: a container restart in the same pod copies the store
            // from the secret again and must not bring the previous token back.
            await UpsertSecretAsync(GitCredentialsSecret(owner, rec.Id, git), ct);
            try
            {
                appliedNow = await _credentialPusher.PushGitCredentialsAsync(podIp, rec.CallbackToken, git.PodPayload(), ct);
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
            {
                // The selection is stored and the next start uses it; failing the request would
                // tell the owner nothing changed when it did.
                _log.LogWarning(error, "Session {Id} did not take the new git credentials", rec.Id);
            }
        }
        // A count rather than the ids: they arrive in the request body, and the record holds them anyway.
        _log.LogInformation("Session {Id} git tokens set ({Count}; applied now: {Now})",
            rec.Id, GitPatSelection.Parse(rec.GitPatIdsJson)?.Count.ToString() ?? "defaults", appliedNow);
        var info = await ToInfoAsync(rec, EffectivePhase(rec, pod), pod?.Status?.PodIP,
            await _browsers.GetSummaryAsync(id, ct), ct);
        return new GitPatSwitchResult(info, appliedNow);
    }

    public async Task<SessionInfo> SwitchSessionCredentialAsync(string owner, string id, string credentialId,
        string? reason, CancellationToken ct = default)
    {
        var rec = await _store.GetAsync(owner, id, ct)
            ?? throw new KeyNotFoundException($"Session {id} not found.");
        var pod = await TryReadPodAsync($"session-{id}", ct);
        SessionCredentialSwitch.Validate(rec, pod?.Status?.Phase ?? rec.Status, pod?.Status?.PodIP, credentialId);
        if (_credentialPusher is null)
            throw new InvalidOperationException("Switching accounts on a running session is not available on this instance.");

        var set = await ReadProviderAccountsAsync(owner, rec.Agent, ct);
        var account = set.Find(credentialId)
            ?? throw new ArgumentException($"No stored {rec.Agent} login with id '{credentialId}'.");
        var file = set.Files[account.Id];

        // Recorded before the push: the pod's watcher uploads the file it was just given as soon
        // as it changes again, and that upload has to land on the new account, not the old one.
        // The resolved id follows for the same reason the spawn writes it: a later limit report
        // from this pod must name the account that is now mounted.
        await _store.SetCredentialIdAsync(rec.Id, account.Id, ct);
        await _store.SetResolvedCredentialIdAsync(rec.Id, account.Id, ct);
        rec.CredentialId = account.Id;
        rec.ResolvedCredentialId = account.Id;
        await _credentialPusher.PushAsync(pod!.Status.PodIP, rec.CallbackToken, rec.Agent, file, reason, ct);

        await MutateProviderAccountsAsync(owner, rec.Agent, latest =>
        {
            if (latest.Find(account.Id) is not { } current) return false;
            current.LastUsedAt = DateTime.UtcNow;
            latest.Dirty = true;
            return true;
        }, ct);
        _log.LogInformation("Switched session {Id} to {Agent} account {Account}", id, rec.Agent, account.Id);
        return await ToInfoAsync(rec, pod.Status.Phase, pod.Status.PodIP, await _browsers.GetSummaryAsync(id, ct), ct);
    }

    public async Task DeleteProviderCredentialsAsync(string owner, AgentKind agent, CancellationToken ct = default)
    {
        try
        {
            await _k8s.CoreV1.DeleteNamespacedSecretAsync(
                ProviderSecretName(owner, agent), _opts.Namespace, cancellationToken: ct);
            _log.LogInformation("Deleted {Agent} login for {Owner}", agent, owner);
        }
        catch (k8s.Autorest.HttpOperationException error)
            when (error.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Already gone. Swallowed rather than surfaced so the endpoint stays idempotent and
            // a caller cannot probe which logins exist by watching the status code.
            _log.LogDebug("No {Agent} login stored for {Owner}", agent, owner);
        }
    }

    // ---------------------------------------------------------------- Create / Resume

    public Task<SessionInfo> CreateSessionAsync(string owner, CreateSessionRequest req, CancellationToken ct = default)
        => CreateSessionCoreAsync(owner, req, allowMigratedClaudeAuto: false, ct);

    private async Task<SessionInfo> CreateSessionCoreAsync(string owner, CreateSessionRequest req, bool allowMigratedClaudeAuto, CancellationToken ct)
    {
        if (req.Mode is SessionMode.Autonomous or SessionMode.Scheduled && string.IsNullOrWhiteSpace(req.Prompt))
            throw new ArgumentException("A prompt is required for Autonomous/Scheduled sessions.");
        if (allowMigratedClaudeAuto)
            AgentConfiguration.ValidateForDuplicatedSession(req.Agent, req.AuthMode, req.OpenClawApiKeySource);
        else
            AgentConfiguration.ValidateForCreate(req.Agent, req.AuthMode, req.OpenClawApiKeySource);
        var uiMode = SessionUiMode.NormalizeForCreate(req.UiMode, req.Agent, req.Mode);

        var image = string.IsNullOrWhiteSpace(req.Image) ? null : req.Image.Trim();
        if (image is not null)
        {
            if (!_opts.AllowCustomImage)
                throw new ArgumentException("Custom images are disabled on this instance.");
            if (image.Length > 300 || !System.Text.RegularExpressions.Regex.IsMatch(image, "^[A-Za-z0-9._/:@-]+$"))
                throw new ArgumentException("Invalid image reference.");
        }
        if (req.RunAsRoot && !_opts.AllowRootSessions)
            throw new ArgumentException("Root sessions are disabled on this instance.");
        ValidateQuantity(req.Cpu, "cpu");
        ValidateQuantity(req.Memory, "memory");
        await ValidateProjectAsync(owner, req.ProjectId, ct);
        await EnsureAgentAllowedAsync(req.Agent, ct);
        await EnforceUsageLimitAsync(owner, req.Agent, req.AuthMode, ct);
        await SessionSoftLimit.EnsureCanCreateAsync(_store, owner, _maxRunningSessionsPerOwner, ct);
        var credentialId = await ValidateCredentialIdAsync(owner, req.Agent, req.AuthMode, req.CredentialId, ct);
        var gitPatIds = await ValidateGitPatIdsAsync(owner, req.GitPatIds, ct);
        var secretIds = await ValidateSecretIdsAsync(owner, req.ProjectId, req.SecretIds, ct);

        var repos = NormalizeRepos(req);
        SessionRepos.Validate(repos);
        await ValidateRepoCredentialsAsync(owner, repos, ct);
        var mcp = string.IsNullOrWhiteSpace(req.McpConfigJson) ? null : req.McpConfigJson;
        // Strict resolve + assemble before Upsert so invalid inline shape or bad catalog
        // config fails closed — no half-created session row.
        var (mcpServerIds, _) = await SessionMcpConfig.ResolveAndAssembleAsync(
            _library, owner, mcp, req.McpServerIds, strict: true, ct);
        // Validate ephemeral sources before allocating a session id / writing a row.
        var preparedEphemeral = PrepareEphemeralSources(req.EphemeralApiSources);
        var policy = EffectivePolicy(req.Policy, req.AllowedTools);
        var (autoDeleteAfter, autoDeleteFrom) = SessionExpiry.ForCreate(req.AutoDeleteAfterSeconds, req.AutoDeleteFrom, req.Mode);

        var id = Guid.NewGuid().ToString("n")[..12];
        var rec = new SessionRecord
        {
            Id = id, Owner = owner, Title = req.Title,
            Description = SessionDescription.Normalize(req.Description),
            Mode = req.Mode, UiMode = uiMode,
            RepoUrl = repos.FirstOrDefault()?.Url, ReposJson = SerializeRepos(repos),
            Schedule = req.Schedule, McpConfigJson = mcp,
            McpServerIdsJson = mcpServerIds.Count == 0 ? null : JsonSerializer.Serialize(mcpServerIds),
            ProjectId = req.ProjectId, ParentSessionId = req.ParentSessionId, Prompt = req.Prompt,
            SystemPrompt = SessionSystemPrompt.Normalize(req.SystemPrompt),
            Agent = req.Agent, AuthMode = req.AuthMode,
            OpenClawApiKeySource = req.Agent == AgentKind.OpenClaw && req.AuthMode == AgentAuthMode.ApiKey
                ? req.OpenClawApiKeySource
                : null,
            CredentialId = credentialId,
            GitPatIdsJson = GitPatSelection.Serialize(gitPatIds),
            SecretIdsJson = SessionSecretSelection.Serialize(secretIds),
            SecretCeilingJson = SessionSecretSelection.Serialize(req.SecretCeiling),
            AgentPolicyJson = SerializePolicy(policy),
            AllowedToolsJson = SerializeAllowedTools(policy.AllowedTools),
            AutoUpdateRuntimeImages = req.AutoUpdateRuntimeImages,
            Image = image, RunAsRoot = req.RunAsRoot,
            AutoApprove = CreateSessionRequest.AutoApproveFor(req.AutoApprove, req.Mode),
            Cpu = req.Cpu, Memory = req.Memory,
            AgentSessionId = Guid.NewGuid().ToString(),
            CallbackToken = RandomToken(),
            Status = req.Mode == SessionMode.Scheduled ? "Scheduled" : "Pending",
            AutoDeleteAfterSeconds = autoDeleteAfter, AutoDeleteFrom = autoDeleteFrom,
            AccountFailover = AccountFailoverMode.Normalize(req.AccountFailover)
        };
        // Persist session row before registering ephemerals so a failed Upsert
        // cannot leave orphaned session-scoped API sources.
        await _store.UpsertAsync(rec, ct);

        try
        {
            await RegisterEphemeralSourcesAsync(owner, id, preparedEphemeral, ct);
            await SpawnAsync(owner, rec, req, resume: false, ct);
        }
        catch
        {
            await _ephemeralApiMcps.DeleteBySessionAsync(id, ct);
            throw;
        }
        return await ToInfoAsync(rec, phase: rec.Status, podIp: null, ct: ct);
    }

    public async Task<SessionInfo> DuplicateSessionAsync(string owner, string id, DuplicateSessionRequest request, CancellationToken ct = default)
    {
        var source = await _store.GetAsync(owner, id, ct)
            ?? throw new KeyNotFoundException($"Session {id} not found.");
        await ValidateProjectAsync(owner, request.ProjectId, ct);
        var copy = SessionDuplication.CopyableRequest(source, request);
        var allowMigratedClaudeAuto = copy.Agent == AgentKind.Claude && copy.AuthMode == AgentAuthMode.Auto;
        return await CreateSessionCoreAsync(owner, copy, allowMigratedClaudeAuto, ct);
    }

    /// <summary>
    /// Fails a create whose repositories could not possibly be cloned, while the caller is still
    /// there to be told.
    ///
    /// Both checks replace the same failure: a credential that silently never materializes. An
    /// unknown or unconnected <c>ProviderId</c> is skipped by <c>BuildCredentialStoreAsync</c>, and
    /// an SSH remote without a stored known_hosts entry meets
    /// <c>StrictHostKeyChecking=yes</c>. Either way the clone fails with an authentication or host
    /// verification error inside an init container whose log an API caller never sees, leaving a
    /// session that is simply broken for no stated reason.
    /// </summary>
    private async Task ValidateRepoCredentialsAsync(
        string owner, IReadOnlyList<RepoRef> repos, CancellationToken ct)
    {
        foreach (var providerId in repos
                     .Select(r => r.ProviderId)
                     .Where(p => !string.IsNullOrWhiteSpace(p))
                     .Select(p => p!.Trim())
                     .Distinct())
        {
            if (!_gitAuth.IsConfigured(providerId))
                throw new ArgumentException($"Unknown Git provider '{providerId}'.");
            if (!await _gitAuth.IsConnectedAsync(owner, providerId, ct))
                throw new ArgumentException(
                    $"Git provider '{providerId}' is not connected for this account. Connect it "
                    + "first, or omit providerId to clone without credentials.");
        }

        if (!repos.Any(r => IsSshRemote(r.Url))) return;
        var creds = (await ReadSecretOrNullAsync(CredsSecretName(owner), ct))?.Data;
        if (creds?.ContainsKey("ssh_key") != true)
            throw new ArgumentException(
                "An SSH repository URL needs a stored SSH private key. Store one, or use an HTTPS "
                + "URL with a connected provider or a personal access token.");
        if (!creds.ContainsKey("known_hosts"))
            throw new ArgumentException(
                "An SSH repository URL needs a stored known_hosts entry — host key checking is "
                + "enforced, so the clone would fail without it.");
    }

    private static bool IsSshRemote(string? url)
    {
        var value = url?.Trim() ?? "";
        if (value.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase)) return true;
        // scp-style user@host:path. Anything with a scheme is already handled above.
        var at = value.IndexOf('@');
        return at > 0 && value.IndexOf(':') > at + 1 && !value.Contains("://", StringComparison.Ordinal);
    }

    // Effective repo list: explicit Repos win; otherwise fold the legacy single-repo fields.
    private static List<RepoRef> NormalizeRepos(CreateSessionRequest req)
    {
        if (req.Repos.Count > 0)
            return req.Repos.Where(r => !string.IsNullOrWhiteSpace(r.Url)).ToList();
        return string.IsNullOrWhiteSpace(req.RepoUrl)
            ? new List<RepoRef>()
            : new List<RepoRef> { new() { Url = req.RepoUrl!, Branch = req.RepoBranch } };
    }

    private static string? SerializeRepos(List<RepoRef> repos) =>
        repos.Count == 0 ? null : JsonSerializer.Serialize(repos);

    private static string? SerializeAllowedTools(IReadOnlyList<string> tools) =>
        tools.Count == 0 ? null : JsonSerializer.Serialize(tools);

    private static AgentPolicy EffectivePolicy(AgentPolicy? policy, IReadOnlyList<string> allowedTools) =>
        AgentConfiguration.ResolvePolicy(policy, allowedTools);

    private static string SerializePolicy(AgentPolicy policy) => JsonSerializer.Serialize(policy);

    private static List<RepoRef> ParseRepos(SessionRecord rec) =>
        string.IsNullOrWhiteSpace(rec.ReposJson)
            ? (string.IsNullOrWhiteSpace(rec.RepoUrl) ? new() : new() { new() { Url = rec.RepoUrl! } })
            : JsonSerializer.Deserialize<List<RepoRef>>(rec.ReposJson) ?? new();

    private static AgentPolicy ParsePolicy(SessionRecord rec)
    {
        if (!string.IsNullOrWhiteSpace(rec.AgentPolicyJson))
            return JsonSerializer.Deserialize<AgentPolicy>(rec.AgentPolicyJson, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new();
        return new AgentPolicy { AllowedTools = ParseAllowedTools(rec) };
    }

    private static List<string> ParseAllowedTools(SessionRecord rec) =>
        string.IsNullOrWhiteSpace(rec.AllowedToolsJson)
            ? new()
            : JsonSerializer.Deserialize<List<string>>(rec.AllowedToolsJson) ?? new();

    private static List<string> ParseMcpServerIds(SessionRecord rec)
    {
        if (string.IsNullOrWhiteSpace(rec.McpServerIdsJson)) return new();
        try { return JsonSerializer.Deserialize<List<string>>(rec.McpServerIdsJson) ?? new(); }
        catch (JsonException) { return new(); }
    }

    /// <summary>Merges catalog servers (lenient: inaccessible ids drop out), the
    /// built-in skill-library MCP server, inline config, and ephemeral API sources
    /// into the effective .mcp.json.</summary>
    private async Task<string?> BuildEffectiveMcpConfigAsync(string owner, SessionRecord rec, CancellationToken ct)
    {
        var gateway = new McpGatewayAssembleOptions
        {
            BaseUrl = _mcpGatewayBaseUrl,
            SessionId = rec.Id,
            IssueToken = mcpServerId => _mcpGatewayTokens.Issue(rec.Id, mcpServerId, owner),
            IssueEphemeralToken = name => _mcpGatewayTokens.IssueEphemeral(rec.Id, name, owner)
        };
        var ephemeral = await _ephemeralApiMcps.ListBySessionAsync(rec.Id, ct);
        var servers = ParseMcpServerIds(rec).Count == 0
            ? (IReadOnlyList<McpServerRecord>)Array.Empty<McpServerRecord>()
            : await _library.ResolveMcpServersAsync(owner, ParseMcpServerIds(rec), strict: false, ct);
        if (_opts.SkillLibraryMcp)
            servers = servers.Append(SkillLibraryMcpConfig.BuildServer(_callbackBaseUrl, rec)).ToList();
        return McpConfigAssembler.Merge(rec.McpConfigJson, servers, gateway, ephemeral);
    }

    private static List<(EphemeralApiSource Source, string ConfigJson, string? SecretJson)> PrepareEphemeralSources(
        IReadOnlyList<EphemeralApiSource> sources)
    {
        var prepared = new List<(EphemeralApiSource, string, string?)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            var name = LibraryValidation.ValidateMcpServerName(source.Name);
            if (!seen.Add(name))
                throw new ArgumentException($"Duplicate ephemeral API source name '{name}'.");

            var configJson = McpServersController.BuildApiConfigJson(new CreateMcpFromApiRequest(
                name, Description: null, source.SpecUrl, source.SpecType, source.BaseUrl, source.Auth,
                source.Secret, Save: false));
            LibraryValidation.ValidateMcpServerConfig(configJson, "api");

            string? secretJson = null;
            if (!string.IsNullOrWhiteSpace(source.Secret))
            {
                var secret = source.Secret.Trim();
                secretJson = secret.StartsWith('{')
                    ? secret
                    : JsonSerializer.Serialize(new Dictionary<string, string> { ["token"] = secret });
            }

            prepared.Add((source with { Name = name }, configJson, secretJson));
        }

        return prepared;
    }

    private async Task RegisterEphemeralSourcesAsync(
        string owner,
        string sessionId,
        IReadOnlyList<(EphemeralApiSource Source, string ConfigJson, string? SecretJson)> prepared,
        CancellationToken ct)
    {
        foreach (var (source, configJson, secretJson) in prepared)
        {
            await _ephemeralApiMcps.RegisterAsync(new EphemeralApiMcpEntry(
                sessionId, source.Name, owner, configJson, secretJson), ct);

            if (!source.SaveToLibrary)
                continue;

            // Upsert by name so a pre-existing personal catalog entry does not
            // abort session create/update.
            await UpsertLibraryApiServerAsync(owner, source.Name, configJson, secretJson, ct);
        }
    }

    private async Task UpsertLibraryApiServerAsync(
        string owner, string name, string configJson, string? secretJson, CancellationToken ct)
    {
        var request = new SaveMcpServerRequest(name, Description: null, Kind: "api", configJson, secretJson);
        try
        {
            var existing = (await _mcpServers.ListByOwnerAsync(owner, ct))
                .FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
                await _mcpServers.UpdateAsync(owner, existing.Id, request, ct);
            else
                await _mcpServers.CreateAsync(owner, request, ct);
        }
        catch (Exception ex)
        {
            // Catalog save is best-effort relative to the session row / ephemerals.
            _log.LogWarning(ex, "SaveToLibrary failed for MCP server {Name}; session continues", name);
        }
    }

    /// <summary>
    /// Replace session ephemerals after assemble validation. Snapshots the previous
    /// set and restores it if registration or a subsequent commit step fails.
    /// </summary>
    private async Task ReplaceEphemeralSourcesAsync(
        string owner,
        string sessionId,
        IReadOnlyList<(EphemeralApiSource Source, string ConfigJson, string? SecretJson)> prepared,
        CancellationToken ct)
    {
        var snapshot = await _ephemeralApiMcps.ListBySessionAsync(sessionId, ct);
        try
        {
            await _ephemeralApiMcps.DeleteBySessionAsync(sessionId, ct);
            await RegisterEphemeralSourcesAsync(owner, sessionId, prepared, ct);
        }
        catch
        {
            await RestoreEphemeralSnapshotAsync(snapshot, ct);
            throw;
        }
    }

    private async Task RestoreEphemeralSnapshotAsync(
        IReadOnlyList<EphemeralApiMcpEntry> snapshot, CancellationToken ct)
    {
        if (snapshot.Count == 0) return;
        var sessionId = snapshot[0].SessionId;
        try
        {
            await _ephemeralApiMcps.DeleteBySessionAsync(sessionId, ct);
            foreach (var entry in snapshot)
                await _ephemeralApiMcps.RegisterAsync(entry, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to restore ephemeral API sources for session {SessionId}", sessionId);
        }
    }

    private static IReadOnlyList<EphemeralApiMcpEntry> ToEphemeralEntries(
        string owner,
        string sessionId,
        IReadOnlyList<(EphemeralApiSource Source, string ConfigJson, string? SecretJson)> prepared) =>
        prepared.Select(p => new EphemeralApiMcpEntry(
            sessionId, p.Source.Name, owner, p.ConfigJson, p.SecretJson)).ToList();

    private async Task ValidateProjectAsync(string owner, string? projectId, CancellationToken ct)
    {
        if (projectId is not null && await _projects.GetAsync(owner, projectId, ct) is null)
            throw new ArgumentException("Project not found.");
    }

    private Task EnsureAgentAllowedAsync(AgentKind agent, CancellationToken ct)
        => AllowedAgentsGuard.EnsureAgentAllowedAsync(_allowedAgents, agent, ct);

    // Monthly API-budget gate. Auto-mode sessions only bill the API when no Claude
    // subscription login is stored, so the stored-login check decides whether Auto counts.
    private async Task EnforceUsageLimitAsync(string owner, AgentKind agent, AgentAuthMode authMode, CancellationToken ct)
    {
        var hasSubscription = agent == AgentKind.Claude &&
            await ReadSecretOrNullAsync(ProviderSecretName(owner, AgentKind.Claude), ct) is not null;
        await _usageLimits.EnsureCanStartAsync(owner, agent, authMode, hasSubscription, ct);
    }

    // Assigns each repo a workspace subdirectory. A single repo keeps the legacy
    // "/workspace/repo" path; multiple repos use their sanitized names (deduped).
    private static IEnumerable<(RepoRef repo, string dest)> DestFor(List<RepoRef> repos)
    {
        if (repos.Count == 1) { yield return (repos[0], "repo"); yield break; }
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in repos)
        {
            var baseName = RepoDirName(r.Url);
            var dest = baseName; var n = 1;
            while (!used.Add(dest)) dest = $"{baseName}-{n++}";
            yield return (r, dest);
        }
    }

    private static string RepoDirName(string url)
    {
        var s = url.TrimEnd('/');
        var slash = s.LastIndexOf('/');
        var name = slash >= 0 ? s[(slash + 1)..] : s;
        if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        name = System.Text.RegularExpressions.Regex.Replace(name, "[^A-Za-z0-9._-]", "-");
        return string.IsNullOrEmpty(name) ? "repo" : name;
    }

    private AgentRuntimeImages ConfiguredRuntimeImages => new(
        string.IsNullOrWhiteSpace(_opts.ClaudeAgentImage) ? _opts.AgentImage : _opts.ClaudeAgentImage,
        _opts.CodexAgentImage, _opts.CursorAgentImage, _opts.OpenClawAgentImage,
        _opts.OpenCodeAgentImage, _opts.AgentImagePullPolicy);

    private bool RuntimeImageOutdated(SessionRecord record, V1Pod? pod) =>
        string.IsNullOrWhiteSpace(record.Image) && pod?.Spec?.Containers?.FirstOrDefault(c => c.Name == "agent") is { } agent &&
        agent.Image != AgentPodSpecFactory.RuntimeImage(record.Agent, ConfiguredRuntimeImages);

    public async Task ReconcileRuntimeImagesAsync(CancellationToken ct)
    {
        var pods = await _k8s.CoreV1.ListNamespacedPodAsync(_opts.Namespace,
            labelSelector: $"{ComponentLabel}=agent", cancellationToken: ct);
        var ids = pods.Items.Select(p => (p.Metadata?.Labels is { } labels && labels.TryGetValue(SessionLabel, out var sid) ? sid : null))
            .Where(id => id is not null).Distinct();
        var cronJobs = await _k8s.BatchV1.ListNamespacedCronJobAsync(_opts.Namespace,
            labelSelector: $"{ComponentLabel}=cronjob", cancellationToken: ct);
        ids = ids.Concat(cronJobs.Items.Select(p => (p.Metadata?.Labels is { } labels && labels.TryGetValue(SessionLabel, out var sid) ? sid : null)))
            .Where(id => id is not null).Distinct();
        foreach (var id in ids)
        {
            try
            {
                var record = await _store.GetByIdAsync(id!, ct);
                if (record is null || !(record.AutoUpdateRuntimeImages ?? _opts.AutoUpdateRuntimeImages)) continue;
                await UpdateRuntimeImageAsync(record.Owner, record.Id, ct);
            }
            catch (InvalidOperationException exception) { _log.LogDebug(exception, "Runtime update deferred for {SessionId}", id); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (HttpRequestException exception) { LogRuntimeUpdateFailure(exception, id); }
            catch (k8s.Autorest.HttpOperationException exception) { LogRuntimeUpdateFailure(exception, id); }
            catch (Npgsql.NpgsqlException exception) { LogRuntimeUpdateFailure(exception, id); }
            catch (Amazon.Runtime.AmazonServiceException exception) { LogRuntimeUpdateFailure(exception, id); }
            catch (KeyNotFoundException exception) { LogRuntimeUpdateFailure(exception, id); }
            catch (ArgumentException exception) { LogRuntimeUpdateFailure(exception, id); }
            catch (OperationCanceledException exception) { LogRuntimeUpdateFailure(exception, id); }
            catch (AgentNotAllowedException exception) { LogRuntimeUpdateFailure(exception, id); }
        }
    }

    private void LogRuntimeUpdateFailure(Exception exception, string? id) =>
        _log.LogWarning(exception, "Runtime update deferred for {SessionId}", id);

    public async Task<SessionInfo> UpdateRuntimeImageAsync(string owner, string id, CancellationToken ct = default)
    {
        if (_runtimeUpdateLock is null) throw new InvalidOperationException("Runtime update lock is unavailable.");
        await using var held = await _runtimeUpdateLock.TryAcquireAsync(id, ct);
        if (held is null) throw new InvalidOperationException("Session maintenance is already in progress.");
        var record = await _store.GetAsync(owner, id, ct) ?? throw new KeyNotFoundException();
        if (!string.IsNullOrWhiteSpace(record.Image))
            throw new InvalidOperationException("Custom runtime images are pinned.");
        var desired = AgentPodSpecFactory.RuntimeImage(record.Agent, ConfiguredRuntimeImages);
        if (record.Mode == SessionMode.Scheduled)
        {
            var cron = await _k8s.BatchV1.ReadNamespacedCronJobAsync($"session-{id}", _opts.Namespace, cancellationToken: ct);
            var containers = cron.Spec.JobTemplate.Spec.Template.Spec.Containers;
            var index = containers.ToList().FindIndex(c => c.Name == "agent");
            if (index < 0) throw new InvalidOperationException("Agent container is missing.");
            if (containers[index].Image != desired)
            {
                // An optimistic patch changes future jobs without replacing active jobs or losing history.
                var operations = new List<object> {
                    new { op = "test", path = "/metadata/resourceVersion", value = cron.Metadata.ResourceVersion },
                    new { op = "replace", path = $"/spec/jobTemplate/spec/template/spec/containers/{index}/image", value = desired }
                };
                var init = cron.Spec.JobTemplate.Spec.Template.Spec.InitContainers;
                for (var i = 0; i < (init?.Count ?? 0); i++)
                    if (init![i].Image == containers[index].Image)
                        operations.Add(new { op = "replace", path = $"/spec/jobTemplate/spec/template/spec/initContainers/{i}/image", value = desired });
                var patch = new V1Patch(JsonSerializer.Serialize(operations), V1Patch.PatchType.JsonPatch);
                await _k8s.BatchV1.PatchNamespacedCronJobAsync(patch, cron.Metadata.Name, _opts.Namespace, cancellationToken: ct);
            }
            return (await GetSessionAsync(owner, id, ct))!;
        }
        var pod = await TryReadPodAsync($"session-{id}", ct);
        if (!RuntimeImageOutdated(record, pod)) return (await GetSessionAsync(owner, id, ct))!;
        if (record.Mode != SessionMode.Interactive || record.Agent != AgentKind.Claude ||
            record.UiMode != SessionUiMode.Chat || record.QuestionPending || pod?.Status?.Phase != SessionStatus.Running ||
            string.IsNullOrEmpty(pod.Status.PodIP) || !_artifacts.IsConfigured)
            throw new InvalidOperationException("Update requires a running idle resumable chat session with saved state and no pending question.");
        await EnsureAgentAllowedAsync(record.Agent, ct);
        await EnforceUsageLimitAsync(owner, record.Agent, record.AuthMode, ct);
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(260) };
        client.DefaultRequestHeaders.Add("X-Agent-Token", record.CallbackToken);
        var drainUrl = $"http://{pod.Status.PodIP}:{_opts.AgentPort}/agenthub/runtime-drain";
        try
        {
            using var admitted = await client.PostAsync(drainUrl, null, ct);
            if (!admitted.IsSuccessStatusCode)
                throw new InvalidOperationException("Runtime is busy or does not support safe updates. Pause and resume manually when idle.");
            record = await _store.GetAsync(owner, id, ct) ?? throw new KeyNotFoundException();
            if (record.QuestionPending) throw new InvalidOperationException("A question is pending.");
            if (await _artifacts.HeadAsync(IArtifactStore.StateKey(owner, id, record.Agent), ct) is null)
                throw new InvalidOperationException("No saved conversation archive is available yet.");
            await PauseSessionCoreAsync(owner, id, ct);
            // Deletion is asynchronous: the old process must finish uploading before resume downloads.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(_opts.PauseGracePeriodSeconds + 30));
            while (await TryReadPodAsync($"session-{id}", deadline.Token) is not null)
                await Task.Delay(250, deadline.Token);
            return await ResumeSessionCoreAsync(owner, id, ct);
        }
        finally
        {
            // A failed maintenance request must not leave a surviving runtime refusing all input.
            try
            {
                using var releaseDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var surviving = await TryReadPodAsync($"session-{id}", releaseDeadline.Token);
                if (surviving is { Metadata: { DeletionTimestamp: null } metadata } && metadata.Uid == pod.Metadata.Uid)
                {
                    using var released = await client.DeleteAsync(drainUrl, releaseDeadline.Token);
                }
            }
            catch (HttpRequestException exception) { _log.LogDebug(exception, "Releasing runtime drain failed"); }
            catch (OperationCanceledException exception) { _log.LogDebug(exception, "Releasing runtime drain timed out"); }
            catch (k8s.Autorest.HttpOperationException exception) { _log.LogDebug(exception, "Reading runtime drain pod failed"); }
        }
    }

    public async Task<SessionInfo> ResumeSessionAsync(string owner, string id, CancellationToken ct = default)
    {
        await using var held = _runtimeUpdateLock is null ? null : await _runtimeUpdateLock.TryAcquireAsync(id, ct);
        if (_runtimeUpdateLock is not null && held is null)
            throw new InvalidOperationException("Session maintenance is already in progress.");
        return await ResumeSessionCoreAsync(owner, id, ct);
    }

    private async Task<SessionInfo> ResumeSessionCoreAsync(string owner, string id, CancellationToken ct)
    {
        var rec = await _store.GetAsync(owner, id, ct)
            ?? throw new KeyNotFoundException($"Session {id} not found.");
        if (rec.Mode == SessionMode.Scheduled)
            throw new ArgumentException("Scheduled sessions are not resumed; they run on their schedule.");
        await EnsureAgentAllowedAsync(rec.Agent, ct);
        await EnforceUsageLimitAsync(owner, rec.Agent, rec.AuthMode, ct);

        await _browsers.StopAsync(id, ct);
        await TryDeletePodAsync($"session-{id}", ct);

        rec.Status = "Pending";
        rec.FailureReason = null;
        rec.QuestionPending = false;
        await _store.UpsertAsync(rec, ct);
        // Somebody wanted this session back: a resume restarts the idle countdown.
        await _store.TouchActivityAsync(id, ct);

        var req = new CreateSessionRequest
        {
            Title = rec.Title, Description = rec.Description, Mode = rec.Mode, UiMode = rec.UiMode,
            Repos = ParseRepos(rec), McpConfigJson = rec.McpConfigJson,
            McpServerIds = ParseMcpServerIds(rec),
            ProjectId = rec.ProjectId, Prompt = rec.Prompt, SystemPrompt = rec.SystemPrompt,
            Agent = rec.Agent, AuthMode = rec.AuthMode, OpenClawApiKeySource = rec.OpenClawApiKeySource,
            CredentialId = rec.CredentialId,
            GitPatIds = GitPatSelection.Parse(rec.GitPatIdsJson)?.ToList(),
            SecretIds = SessionSecretSelection.Parse(rec.SecretIdsJson)?.ToList(),
            Policy = ParsePolicy(rec),
            AllowedTools = ParseAllowedTools(rec),
            Image = rec.Image, RunAsRoot = rec.RunAsRoot, AutoApprove = rec.AutoApprove,
            Cpu = rec.Cpu, Memory = rec.Memory
        };
        await SpawnAsync(owner, rec, req, resume: true, ct);
        _log.LogInformation("Resuming session {Id} (claudeSessionId={Csid})", id, rec.AgentSessionId);
        return await ToInfoAsync(rec, phase: rec.Status, podIp: null, ct: ct);
    }

    /// <summary>
    /// Turns a finished or paused autonomous session into an interactive one and resumes it
    /// (docs/session-mode-conversion.md). The conversion is an edit of the record followed by
    /// the ordinary resume: the pod sees AGENTHUB_MODE=interactive and AGENTHUB_RESUME=1, and the
    /// drivers already continue the conversation on those two values where the CLI can.
    /// </summary>
    public async Task<SessionInfo> ConvertSessionAsync(string owner, string id, ConvertSessionRequest req,
        CancellationToken ct = default)
    {
        var rec = await _store.GetAsync(owner, id, ct)
            ?? throw new KeyNotFoundException($"Session {id} not found.");
        var pod = await TryReadPodAsync($"session-{id}", ct);
        // The live pod's phase, as everywhere else: a record still saying Running for a pod that
        // has since finished must not refuse the conversion, and a stored Succeeded next to a pod
        // that is somehow still alive must not allow it.
        var phase = SessionStatus.ResolvePhase(pod?.Status?.Phase, rec.Status);
        var plan = SessionConversion.Validate(rec, phase, req);

        SessionConversion.Apply(rec, plan);
        await _store.UpsertAsync(rec, ct);
        _log.LogInformation("Converted session {Id} from {From} to interactive (ui={UiMode}, autoApprove={AutoApprove})",
            id, rec.ConvertedFrom, rec.UiMode, rec.AutoApprove);

        // Persisted before the resume on purpose: if the resume is refused (agent no longer
        // allowed, usage limit), the session is still interactive and the ordinary Resume button
        // finishes the job, instead of a session that looks autonomous and resumes interactive.
        if (req.Resume) return await ResumeSessionAsync(owner, id, ct);
        return await ToInfoAsync(rec, phase, podIp: null, await _browsers.GetSummaryAsync(id, ct), ct);
    }

    /// <summary>
    /// Pauses a session: deleting the pod sends SIGTERM, whereupon the agent uploads
    /// its Claude state + scrollback to S3 during the grace period (see server.js).
    /// The session is marked "Paused" and can later be resumed from that saved state.
    /// </summary>
    public async Task<SessionInfo> PauseSessionAsync(string owner, string id, CancellationToken ct = default)
    {
        await using var held = _runtimeUpdateLock is null ? null : await _runtimeUpdateLock.TryAcquireAsync(id, ct);
        if (_runtimeUpdateLock is not null && held is null)
            throw new InvalidOperationException("Session maintenance is already in progress.");
        return await PauseSessionCoreAsync(owner, id, ct);
    }

    private async Task<SessionInfo> PauseSessionCoreAsync(string owner, string id, CancellationToken ct)
    {
        var rec = await _store.GetAsync(owner, id, ct)
            ?? throw new KeyNotFoundException($"Session {id} not found.");
        if (rec.Mode == SessionMode.Scheduled)
            throw new ArgumentException("Scheduled sessions cannot be paused; they run on their schedule.");

        await _browsers.StopAsync(id, ct);
        // Helpers do not survive a pause: their storage is emptyDir, and a paused session holding
        // a database's quota would keep it from every session that runs.
        if (_containerCleanup is not null) await _containerCleanup.CleanupSessionAsync(id, ct);

        // Longer grace than a plain delete so the graceful state upload can finish
        // before the container is killed (the k8s default of 30s is plenty; the
        // agent uploads state, then exits).
        await TryDeletePodAsync($"session-{id}", ct, _opts.PauseGracePeriodSeconds);
        if (_fileCleanup is not null)
        {
            await _fileCleanup.ExpirePodFilesAsync(id, ct);
        }

        rec.Status = SessionStatus.Paused;
        rec.QuestionPending = false;
        await _store.UpsertAsync(rec, ct);
        _log.LogInformation("Paused session {Id} (pod removed, state uploaded to S3)", id);
        return await ToInfoAsync(rec, phase: SessionStatus.Paused, podIp: null, ct: ct);
    }

    /// <summary>
    /// Updates stored session settings. The title applies immediately; everything
    /// else takes effect the next time the session is resumed (pod is rebuilt).
    /// </summary>
    public async Task<SessionInfo> UpdateSessionAsync(string owner, string id, UpdateSessionRequest req, CancellationToken ct = default)
    {
        // An edit must not change the agent or image between drain admission and resume.
        await using var held = _runtimeUpdateLock is null ? null : await _runtimeUpdateLock.TryAcquireAsync(id, ct);
        if (_runtimeUpdateLock is not null && held is null)
            throw new InvalidOperationException("Session maintenance is already in progress.");
        return await UpdateSessionCoreAsync(owner, id, req, ct);
    }

    private async Task<SessionInfo> UpdateSessionCoreAsync(string owner, string id, UpdateSessionRequest req, CancellationToken ct)
    {
        var rec = await _store.GetAsync(owner, id, ct)
            ?? throw new KeyNotFoundException($"Session {id} not found.");
        SessionUpdateValidator.Validate(rec, req);
        if (req.AutoUpdateRuntimeImagesSpecified) rec.AutoUpdateRuntimeImages = req.AutoUpdateRuntimeImages;
        if (req.Agent is { } requestedAgent)
            await EnsureAgentAllowedAsync(requestedAgent, ct);

        if (!string.IsNullOrWhiteSpace(req.Title))
            rec.Title = req.Title.Trim();
        // null = unchanged; an empty string clears the description.
        if (req.Description is not null)
            rec.Description = SessionDescription.Normalize(req.Description);
        // Same convention: null leaves the prompt alone, an empty string removes it. Normalize
        // applies the create-side cap, so an oversized prompt is a 400 here and not a pod spec
        // the API server refuses on the next resume.
        if (req.SystemPrompt is not null)
            rec.SystemPrompt = SessionSystemPrompt.Normalize(req.SystemPrompt);
        if (req.Image is not null)
        {
            // Empty string resets to the default agent image.
            var image = string.IsNullOrWhiteSpace(req.Image) ? null : req.Image.Trim();
            if (image is not null)
            {
                if (!_opts.AllowCustomImage)
                    throw new ArgumentException("Custom images are disabled on this instance.");
                if (image.Length > 300 || !System.Text.RegularExpressions.Regex.IsMatch(image, "^[A-Za-z0-9._/:@-]+$"))
                    throw new ArgumentException("Invalid image reference.");
            }
            rec.Image = image;
        }
        if (req.RunAsRoot is { } asRoot)
        {
            if (asRoot && !_opts.AllowRootSessions)
                throw new ArgumentException("Root sessions are disabled on this instance.");
            rec.RunAsRoot = asRoot;
        }
        // Unlike the fields around it this needs no respawn: the permission endpoint reads
        // the flag off the record on every request, so it applies to the live session.
        if (req.AutoApprove is { } autoApprove) rec.AutoApprove = autoApprove;
        if (req.Cpu is not null) { ValidateQuantity(req.Cpu, "cpu"); rec.Cpu = req.Cpu; }
        if (req.Memory is not null) { ValidateQuantity(req.Memory, "memory"); rec.Memory = req.Memory; }
        if (req.Repos is not null)
        {
            var repos = req.Repos.Where(r => !string.IsNullOrWhiteSpace(r.Url)).ToList();
            // Same checks as on create: an edited list takes effect on the next start, so an
            // unconnected provider or an SSH URL without a known_hosts entry would otherwise turn
            // a working session into one that fails to come back.
            SessionRepos.Validate(repos);
            await ValidateRepoCredentialsAsync(owner, repos, ct);
            rec.ReposJson = SerializeRepos(repos);
            rec.RepoUrl = repos.FirstOrDefault()?.Url;
        }
        // Validate project before mutating ephemerals / MCP secret.
        if (req.ProjectIdSpecified)
        {
            await ValidateProjectAsync(owner, req.ProjectId, ct);
            rec.ProjectId = req.ProjectId;
        }
        var mcpDirty = false;
        if (req.McpConfigJson is not null)
        {
            // Empty string clears inline MCP config; catalog ids are unchanged unless also sent.
            rec.McpConfigJson = string.IsNullOrWhiteSpace(req.McpConfigJson) ? null : req.McpConfigJson;
            mcpDirty = true;
        }
        if (req.McpServerIds is not null)
            mcpDirty = true;
        // null = leave session ephemerals alone; non-null replaces the full set (empty clears).
        // Validate/prepare first; mutate the store only after assemble succeeds.
        IReadOnlyList<(EphemeralApiSource Source, string ConfigJson, string? SecretJson)>? preparedEphemeral = null;
        if (req.EphemeralApiSources is not null)
        {
            preparedEphemeral = PrepareEphemeralSources(req.EphemeralApiSources);
            mcpDirty = true;
        }
        IReadOnlyList<EphemeralApiMcpEntry>? ephemeralSnapshot = null;
        if (mcpDirty)
        {
            // Strict resolve + assemble with the *proposed* ephemeral set before any
            // DeleteBySession so a bad catalog id / inline shape cannot wipe sources.
            var idsToResolve = req.McpServerIds ?? ParseMcpServerIds(rec);
            var gateway = new McpGatewayAssembleOptions
            {
                BaseUrl = _mcpGatewayBaseUrl,
                SessionId = rec.Id,
                IssueToken = mcpServerId => _mcpGatewayTokens.Issue(rec.Id, mcpServerId, owner),
                IssueEphemeralToken = name => _mcpGatewayTokens.IssueEphemeral(rec.Id, name, owner)
            };
            var ephemeral = preparedEphemeral is not null
                ? ToEphemeralEntries(owner, rec.Id, preparedEphemeral)
                : await _ephemeralApiMcps.ListBySessionAsync(rec.Id, ct);
            var servers = await _library.ResolveMcpServersAsync(owner, idsToResolve, strict: true, ct);
            var ids = servers.Select(s => s.Id).ToList();
            var effective = McpConfigAssembler.Merge(rec.McpConfigJson, servers, gateway, ephemeral);
            if (req.McpServerIds is not null)
                rec.McpServerIdsJson = ids.Count == 0 ? null : JsonSerializer.Serialize(ids);

            if (preparedEphemeral is not null)
            {
                ephemeralSnapshot = await _ephemeralApiMcps.ListBySessionAsync(rec.Id, ct);
                await ReplaceEphemeralSourcesAsync(owner, rec.Id, preparedEphemeral, ct);
            }

            try
            {
                if (effective is not null)
                    await CreateMcpSecretAsync(owner, id, effective, ct);
                else
                    try { await _k8s.CoreV1.DeleteNamespacedSecretAsync($"mcp-{id}", _opts.Namespace, cancellationToken: ct); } catch { }
            }
            catch
            {
                if (ephemeralSnapshot is not null)
                    await RestoreEphemeralSnapshotAsync(ephemeralSnapshot, ct);
                throw;
            }
        }
        var previousAgent = rec.Agent;
        if (req.Agent is { } agent)
            rec.Agent = agent;
        if (req.AuthMode is { } authMode)
            rec.AuthMode = authMode;
        if (rec.Agent == AgentKind.OpenClaw && rec.AuthMode == AgentAuthMode.ApiKey)
        {
            if (req.OpenClawApiKeySource is { } source)
                rec.OpenClawApiKeySource = source;
        }
        else if (req.Agent is not null || req.AuthMode is not null)
        {
            rec.OpenClawApiKeySource = null;
        }
        // An account is bound to one provider: a change of agent drops the pin unless the
        // request names an account of the new agent in the same breath.
        if (req.CredentialId is not null)
            rec.CredentialId = await ValidateCredentialIdAsync(owner, rec.Agent, rec.AuthMode, req.CredentialId, ct);
        else if (rec.Agent != previousAgent || rec.AuthMode == AgentAuthMode.ApiKey)
            rec.CredentialId = null;
        if (req.GitPatIds is not null)
            rec.GitPatIdsJson = GitPatSelection.Serialize(await ValidateGitPatIdsAsync(owner, req.GitPatIds, ct));
        // Checked against the project the session is in after this update, so moving a session and
        // narrowing its secrets in one request is judged by the project it ends up in.
        if (req.SecretIds is not null)
            rec.SecretIdsJson = SessionSecretSelection.Serialize(
                await ValidateSecretIdsAsync(owner, rec.ProjectId, req.SecretIds, ct));
        // Grants can only be taken back here, never added: adding is answering a request, which
        // checks the limit the session was created with.
        if (req.SecretGrantIds is not null)
        {
            var current = SessionSecretSelection.Parse(rec.SecretGrantIdsJson) ?? [];
            var kept = current.Where(req.SecretGrantIds.Contains).ToList();
            rec.SecretGrantIdsJson = kept.Count == 0 ? null : SessionSecretSelection.Serialize(kept);
        }
        if (req.Policy is { } policy)
        {
            rec.AgentPolicyJson = SerializePolicy(policy);
            rec.AllowedToolsJson = SerializeAllowedTools(policy.AllowedTools);
        }
        if (req.AutoDeleteAfterSeconds is not null || req.AutoDeleteFrom is not null)
            (rec.AutoDeleteAfterSeconds, rec.AutoDeleteFrom) =
                SessionExpiry.ForUpdate(rec, req.AutoDeleteAfterSeconds, req.AutoDeleteFrom);
        // Read off the record when a limit is reported, so it applies to the live session.
        if (req.AccountFailover is not null)
            rec.AccountFailover = AccountFailoverMode.Normalize(req.AccountFailover);

        try
        {
            await _store.UpsertAsync(rec, ct);
        }
        catch
        {
            if (ephemeralSnapshot is not null)
                await RestoreEphemeralSnapshotAsync(ephemeralSnapshot, ct);
            throw;
        }
        _log.LogInformation("Updated session {Id} settings", id);
        // Files follow at once in a running session; variables are fixed when its container starts.
        if (req.SecretIds is not null || req.SecretGrantIds is not null || req.ProjectIdSpecified)
            await RefreshSessionSecretsAsync(owner, rec, ct);

        var pod = await TryReadPodAsync($"session-{id}", ct);
        await ObserveFailureAsync(rec, pod, ct);
        return await ToInfoAsync(rec, EffectivePhase(rec, pod), pod?.Status?.PodIP,
            await _browsers.GetSummaryAsync(id, ct), ct);
    }

    private static void ValidateQuantity(string value, string what)
    {
        try { _ = new ResourceQuantity(value).ToDecimal(); }
        catch { throw new ArgumentException($"Invalid {what} quantity: '{value}'."); }
    }

    /// <summary>
    /// The session's credential store and per-host authors: connected-provider lines for the
    /// repositories it names, then the stored PATs its selection keeps (one per host). Null when
    /// there is nothing to write. Shared by the spawn and the switch of a running session, so the
    /// pod gets the same content either way.
    /// </summary>
    private async Task<SessionGitCredentials?> BuildGitCredentialsAsync(string owner, SessionRecord rec,
        IReadOnlyList<RepoRef> repos, CancellationToken ct)
    {
        var oauthStore = await _gitAuth.BuildCredentialStoreAsync(owner, repos, ct);
        var manualCredentials = (await ReadSecretOrNullAsync(CredsSecretName(owner), ct))?.Data;
        var selectedPats = GitPatSelection.Apply(
            GitPatStore.Read(manualCredentials), GitPatSelection.Parse(rec.GitPatIdsJson));
        var store = ManualGitCredentials.ComposeStore(oauthStore, ManualGitCredentials.Lines(selectedPats));
        return store is null ? null : new SessionGitCredentials(store, ManualGitCredentials.Authors(selectedPats, oauthStore));
    }

    private V1Secret GitCredentialsSecret(string owner, string sessionId, SessionGitCredentials git) => new()
    {
        Metadata = new V1ObjectMeta
        {
            Name = $"gitcreds-{sessionId}", NamespaceProperty = _opts.Namespace,
            Labels = new Dictionary<string, string> { [OwnerLabel] = Sanitize(owner), [SessionLabel] = sessionId }
        },
        Type = "Opaque",
        Data = new Dictionary<string, byte[]>
        {
            ["credentials"] = Encoding.UTF8.GetBytes(git.Store),
            ["authors"] = JsonSerializer.SerializeToUtf8Bytes(git.Authors, SessionGitCredentials.Json)
        }
    };

    private async Task SpawnAsync(string owner, SessionRecord rec, CreateSessionRequest req, bool resume, CancellationToken ct)
    {
        // Resolved fresh on every (re)start so catalog entries stay live:
        // an updated shared server lands in the pod on the next resume, and
        // entries that are no longer accessible (revoked share, lapsed license)
        // silently drop out. Own entries always survive.
        var effectiveMcp = await BuildEffectiveMcpConfigAsync(owner, rec, ct);
        req = req with { McpConfigJson = effectiveMcp };

        var context = await BuildPodContextAsync(owner, rec, resume, hasGitCredentials: false, ct);
        var preparation = await AgentSessionResourceOrchestrator.PrepareAsync(
            rec,
            context,
            async (diagnostic, resourceCt) =>
            {
                rec.Status = "Failed";
                rec.FailureReason = SessionStartupFailure.Redact(diagnostic);
                await _store.UpsertAsync(rec, resourceCt);
                await _store.SetScrollbackAsync(rec.Id, rec.FailureReason, resourceCt);
                _log.LogWarning("Session {Id} failed credential preflight for {Agent}/{AuthMode}",
                    rec.Id, rec.Agent, rec.AuthMode);
            },
            async resourceCt =>
            {
                if (effectiveMcp is not null)
                    await CreateMcpSecretAsync(owner, rec.Id, effectiveMcp, resourceCt);
                else
                    try { await _k8s.CoreV1.DeleteNamespacedSecretAsync($"mcp-{rec.Id}", _opts.Namespace, cancellationToken: resourceCt); } catch { }

                // Connected Git-provider credentials are session-scoped and must only be
                // materialized after credential preflight succeeds.
                //
                // Manually stored PATs join the same store. They used to be installed in the pod as
                // a global credential helper, which offered the token to any host that answered
                // 401; a store entry is bound to one host. It also means the raw token no longer
                // has to be projected into the pod at all — see ManualGitCredentials.
                //
                // Which PATs is read from the record, not the request: a resume rebuilds the
                // request from the record anyway, and one source means a resumed session cannot
                // come back with a different selection than it was created with.
                var git = await BuildGitCredentialsAsync(owner, rec, NormalizeRepos(req), resourceCt);
                if (git is null) return false;
                await UpsertSecretAsync(GitCredentialsSecret(owner, rec.Id, git), resourceCt);
                return true;
            },
            ct);
        if (!preparation.ShouldSpawn) return;

        context = context with { HasGitCredentials = preparation.HasGitCredentials,
            HasSessionSecrets = true,
            SecretEnvNames = await WriteSessionSecretsAsync(owner, rec,
                (await ReadSecretOrNullAsync(UserSecretsName(owner), ct))?.Data, ct)
        };
        var podSpec = AgentPodSpecFactory.Build(rec, req, context);

        if (rec.Mode == SessionMode.Scheduled)
        {
            await _k8s.BatchV1.CreateNamespacedCronJobAsync(new V1CronJob
            {
                Metadata = Meta($"session-{rec.Id}", owner, rec.Id, "cronjob", rec.Title),
                Spec = new V1CronJobSpec
                {
                    Schedule = req.Schedule ?? throw new ArgumentException("Schedule is missing."),
                    ConcurrencyPolicy = "Forbid",
                    SuccessfulJobsHistoryLimit = 3, FailedJobsHistoryLimit = 3,
                    JobTemplate = new V1JobTemplateSpec
                    {
                        Spec = new V1JobSpec
                        {
                            BackoffLimit = 0, ActiveDeadlineSeconds = 60 * 60 * 6,
                            Template = new V1PodTemplateSpec
                            {
                                Metadata = Meta($"session-{rec.Id}", owner, rec.Id, "agent", rec.Title),
                                Spec = podSpec
                            }
                        }
                    }
                }
            }, _opts.Namespace, cancellationToken: ct);
        }
        else
        {
            try
            {
                await _k8s.CoreV1.CreateNamespacedPodAsync(new V1Pod
                {
                    Metadata = Meta($"session-{rec.Id}", owner, rec.Id, "agent", rec.Title),
                    Spec = podSpec
                }, _opts.Namespace, cancellationToken: ct);
            }
            catch (k8s.Autorest.HttpOperationException ex) when ((int)ex.Response.StatusCode is 400 or 403 or 422)
            {
                rec.FailureReason = SessionStartupFailure.Admission(ex.Response.Content ?? ex.Message).Message;
                rec.Status = SessionStatus.Failed;
                await _store.SetFailureReasonAsync(rec.Id, rec.FailureReason, true, ct);
            }
        }
    }

    // ---------------------------------------------------------------- List / Get / Transcript / Delete

    public async Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(string owner, CancellationToken ct = default)
    {
        var records = await _store.ListAsync(owner, ct);
        // resourceVersion "0" answers from the apiserver's watch cache. Left unset this is a
        // quorum read against etcd, and because etcd cannot index labels the apiserver fetches
        // every pod in the namespace before applying the selector — so the dashboard poll, which
        // repeats this every five seconds per open tab, scaled with the total pod count rather
        // than with the caller's sessions. The staleness that buys is milliseconds, and the phase
        // is already treated as advisory below: a missing pod falls back to the stored status.
        var pods = await _k8s.CoreV1.ListNamespacedPodAsync(_opts.Namespace,
            labelSelector: $"{OwnerLabel}={Sanitize(owner)},{ComponentLabel}=agent",
            resourceVersion: "0", cancellationToken: ct);
        var byId = new Dictionary<string, V1Pod>();
        foreach (var p in pods.Items)
            if (p.Metadata.Labels is { } labels && labels.TryGetValue(SessionLabel, out var sid))
                byId[sid] = p;

        var scheduledTemplates = new Dictionary<string, V1PodSpec>();
        if (records.Any(r => r.Mode == SessionMode.Scheduled))
        {
            var schedules = await _k8s.BatchV1.ListNamespacedCronJobAsync(_opts.Namespace,
                labelSelector: $"{OwnerLabel}={Sanitize(owner)},{ComponentLabel}=cronjob",
                resourceVersion: "0", cancellationToken: ct);
            foreach (var cron in schedules.Items.Where(cron => cron.Metadata.Labels.ContainsKey(SessionLabel)))
                scheduledTemplates[cron.Metadata.Labels[SessionLabel]] = cron.Spec.JobTemplate.Spec.Template.Spec;
        }
        var browserSummaries = await _browsers.GetSummariesAsync(
            records.Select(record => record.Id).ToArray(), ct);
        var withEphemeralMcp = await _ephemeralApiMcps.ListSessionsWithEntriesAsync(
            records.Select(record => record.Id).ToArray(), ct);
        var result = new List<SessionInfo>();
        foreach (var r in records)
        {
            byId.TryGetValue(r.Id, out var pod);
            await ObserveFailureAsync(r, pod, ct);
            result.Add(ToInfo(r, EffectivePhase(r, pod), pod?.Status?.PodIP, browserSummaries[r.Id],
                withEphemeralMcp.Contains(r.Id)) with { OutdatedImage = RuntimeImageOutdated(r,
                    pod ?? (scheduledTemplates.TryGetValue(r.Id, out var template) ? new V1Pod { Spec = template } : null)) });
        }
        return result;
    }

    public async Task<SessionInfo?> GetSessionAsync(string owner, string id, CancellationToken ct = default)
    {
        var rec = await _store.GetAsync(owner, id, ct);
        if (rec is null) return null;
        var pod = await TryReadPodAsync($"session-{id}", ct);
        await ObserveFailureAsync(rec, pod, ct);
        var info = await ToInfoAsync(rec, EffectivePhase(rec, pod), pod?.Status?.PodIP,
            await _browsers.GetSummaryAsync(id, ct), ct);
        if (pod is null && rec.Mode == SessionMode.Scheduled)
        {
            var cron = await _k8s.BatchV1.ReadNamespacedCronJobAsync($"session-{id}", _opts.Namespace, cancellationToken: ct);
            pod = new V1Pod { Spec = cron.Spec.JobTemplate.Spec.Template.Spec };
        }
        return info with { OutdatedImage = RuntimeImageOutdated(rec, pod) };
    }

    public async Task ClearQuestionAsync(string owner, string id, CancellationToken ct = default)
    {
        if (await _store.GetAsync(owner, id, ct) is not null)
            await _store.SetQuestionPendingAsync(id, false, ct);
    }

    public async Task TouchActivityAsync(string owner, string id, CancellationToken ct = default)
    {
        if (await _store.GetAsync(owner, id, ct) is not null)
            await _store.TouchActivityAsync(id, ct);
    }

    public async Task<string?> GetTranscriptAsync(string owner, string id, CancellationToken ct = default)
        => AgentTerminal.CleanTranscript(await GetScrollbackAsync(owner, id, ct));

    public async Task<IReadOnlyList<TranscriptEntry>?> GetConversationAsync(string owner, string id, CancellationToken ct = default)
    {
        if (await _store.GetAsync(owner, id, ct) is not { } rec) return null;
        var key = IArtifactStore.TranscriptKey(Sanitize(owner), id);
        var jsonl = await TranscriptReader.ReadTranscriptAsync(
            token => _artifacts.GetTextAsync(key, token),
            token => _store.GetTranscriptAsync(id, token),
            ct);
        // A chat session's scrollback *is* stream-json — the same line shape the native file
        // has — so a chat session from before native transcripts were uploaded still gets a
        // readable conversation instead of raw JSON on the remote API and the MCP tools.
        if (string.IsNullOrEmpty(jsonl) && string.Equals(rec.UiMode, "chat", StringComparison.OrdinalIgnoreCase))
            jsonl = await GetScrollbackAsync(owner, id, ct);
        if (string.IsNullOrEmpty(jsonl)) return null;
        var entries = NativeTranscript.Parse(jsonl);
        return entries.Count == 0 ? null : entries;
    }

    public async Task<string?> GetScrollbackAsync(string owner, string id, CancellationToken ct = default)
    {
        if (await _store.GetAsync(owner, id, ct) is null) return null;
        // Both copies, so an S3 copy frozen by an expired upload url does not hide the current
        // one; on instances without S3 the Postgres copy is all there is.
        var key = IArtifactStore.ScrollbackKey(Sanitize(owner), id);
        return await TranscriptReader.ReadScrollbackAsync(
            token => _artifacts.GetTextAsync(key, token),
            token => _store.GetScrollbackAsync(id, token),
            ct);
    }

    public async Task<Stream?> OpenStateArchiveAsync(string owner, string id, CancellationToken ct = default)
    {
        if (await _store.GetAsync(owner, id, ct) is not { } rec) return null;
        return await _artifacts.OpenReadAsync(
            IArtifactStore.StateKey(Sanitize(owner), rec.Id, rec.Agent), ct);
    }

    /// <summary>
    /// Replaces the saved state a resume unpacks into the pod. Refused while a pod is live:
    /// that pod writes its own state over the same key when it stops, so an upload accepted
    /// now would vanish at the next pause with nothing to show that it had been lost.
    /// </summary>
    public async Task<bool> ReplaceStateArchiveAsync(string owner, string id, Stream content,
        long? contentLength, CancellationToken ct = default)
    {
        var rec = await _store.GetAsync(owner, id, ct)
            ?? throw new KeyNotFoundException($"Session {id} not found.");
        var pod = await TryReadPodAsync($"session-{id}", ct);
        var phase = SessionStatus.ResolvePhase(pod?.Status?.Phase, rec.Status);
        if (!SessionStatus.CanReplaceState(phase))
            throw new InvalidOperationException(
                "Pause the session before uploading its state; a running pod overwrites it on exit.");

        var stored = await _artifacts.TryPutStreamAsync(
            IArtifactStore.StateKey(Sanitize(owner), rec.Id, rec.Agent),
            content, "application/gzip", contentLength, ct);
        if (stored) _log.LogInformation("Replaced stored state of session {Id} from an upload", id);
        return stored;
    }

    public async Task<string?> MintArtifactUploadUrlAsync(string sessionId, string token, string name, CancellationToken ct = default)
    {
        var rec = await _store.GetByCallbackTokenAsync(token, ct);
        if (rec is null || rec.Id != sessionId) return null;
        var key = IArtifactStore.ArtifactKey(Sanitize(rec.Owner), rec.Id, name);
        return _artifacts.PresignPut(key, PresignTtl);
    }

    // The urls a pod got at start expire after PresignTtl, and a pod outlives that easily. Past
    // it every upload was refused with a 403 the pod could not act on: the state archive, the
    // scrollback and the transcript in object storage all froze at the twelve-hour mark (#111).
    public async Task<AgentSessionArtifactUrls?> RefreshArtifactUrlsAsync(string sessionId, string token, CancellationToken ct = default)
    {
        var rec = await _store.GetByCallbackTokenAsync(token, ct);
        if (rec is null || rec.Id != sessionId) return null;
        return AgentSessionResourceOrchestrator.PresignArtifactUrls(_artifacts, Sanitize(rec.Owner), rec, resume: false, PresignTtl);
    }

    public async Task DeleteSessionAsync(string owner, string id, CancellationToken ct = default)
    {
        // A delete racing the pause's upsert could otherwise recreate the deleted session.
        await using var held = _runtimeUpdateLock is null ? null : await _runtimeUpdateLock.TryAcquireAsync(id, ct);
        if (_runtimeUpdateLock is not null && held is null)
            throw new InvalidOperationException("Session maintenance is already in progress.");
        await DeleteSessionCoreAsync(owner, id, ct);
    }

    internal async Task DeleteSessionCoreAsync(string owner, string id, CancellationToken ct)
    {
        var rec = await _store.GetAsync(owner, id, ct)
            ?? throw new KeyNotFoundException($"Session {id} not found.");
        if (_fileCleanup is not null)
        {
            var liveSession = await GetSessionAsync(owner, id, ct);
            await _fileCleanup.DeleteSessionAsync(id, liveSession, ct);
        }
        await _browsers.StopAsync(id, ct);
        await _browsers.DeleteStateAsync(rec, ct);
        // Dynamic port-request NetworkPolicies (and their grant rows) die with the session.
        if (_networkCleanup is not null) await _networkCleanup.CleanupSessionAsync(id, ct);
        if (_containerCleanup is not null) await _containerCleanup.CleanupSessionAsync(id, ct);
        await TryDeletePodAsync($"session-{id}", ct);
        try { await _k8s.BatchV1.DeleteNamespacedCronJobAsync($"session-{id}", _opts.Namespace, propagationPolicy: "Foreground", cancellationToken: ct); } catch { }
        try { await _k8s.CoreV1.DeleteNamespacedSecretAsync($"mcp-{id}", _opts.Namespace, cancellationToken: ct); } catch { }
        try { await _k8s.CoreV1.DeleteNamespacedSecretAsync($"gitcreds-{id}", _opts.Namespace, cancellationToken: ct); } catch { }
        await DeleteSecretIfPresentAsync(SessionSecretDeliveryFactory.FilesSecretName(id), ct);
        await DeleteSecretIfPresentAsync(SessionSecretDeliveryFactory.EnvSecretName(id), ct);
        if (_secretRequests is not null) await _secretRequests.DeleteBySessionAsync(id, ct);
        await _ephemeralApiMcps.DeleteBySessionAsync(id, ct);
        await _store.DeleteAsync(id, ct);
        _log.LogInformation("Deleted session {Id}", id);
    }

    /// <summary>
    /// Deletes every secret labeled with the owner (creds-*, provider logins, gitauth-*, the
    /// session secret store, plus any leftover per-session secrets) — the Kubernetes part of an
    /// account purge.
    ///
    /// One delete per secret rather than a deletecollection: the chart's role grants get, list and
    /// delete on secrets but not deletecollection, and against a real API server the collection
    /// call was refused with 403, which stopped the purge before it reached object storage.
    /// </summary>
    public async Task DeleteUserSecretsAsync(string owner, CancellationToken ct = default)
    {
        var owned = await _k8s.CoreV1.ListNamespacedSecretAsync(_opts.Namespace,
            labelSelector: $"{OwnerLabel}={Sanitize(owner)}", cancellationToken: ct);
        foreach (var secret in owned.Items)
            await DeleteSecretIfPresentAsync(secret.Metadata.Name, ct);
        _log.LogInformation("Deleted per-user secrets for an account purge");
    }

    /// <summary>Deletes a secret; one that is already gone is what was asked for. Any other failure
    /// is the caller's to see: a purge or delete that silently left a secret behind would claim
    /// to have removed something it did not.</summary>
    private async Task DeleteSecretIfPresentAsync(string name, CancellationToken ct)
    {
        try
        {
            await _k8s.CoreV1.DeleteNamespacedSecretAsync(name, _opts.Namespace, cancellationToken: ct);
        }
        catch (k8s.Autorest.HttpOperationException e) when (e.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // The name is built from a route id and stays out of the log line, where a crafted
            // value could forge entries.
            _log.LogDebug("A secret to delete was already gone");
        }
    }

    /// <summary>Stable, non-reversible owner key used in labels and S3 key prefixes.</summary>
    public static string OwnerKey(string owner) => Sanitize(owner);

    // ---------------------------------------------------------------- Pod-Spec

    private async Task<PodBuildContext> BuildPodContextAsync(
        string owner, SessionRecord record, bool resume, bool hasGitCredentials, CancellationToken ct)
    {
        var hasApiKey = false;
        var hasSubscription = false;
        string? accountId = null;
        if (record.AuthMode is AgentAuthMode.Subscription or AgentAuthMode.Auto)
        {
            // Resolved at every start, so a session without a pinned account follows the default
            // and a pinned one that was removed since is reported, not silently swapped. An
            // unpinned session also steps around an account at its usage limit; a pinned one is
            // mounted as pinned (docs/account-limits.md).
            var accounts = await ReadProviderAccountsAsync(owner, record.Agent, ct);
            accountId = record.CredentialId is not null
                ? accounts.Find(record.CredentialId)?.Id
                : ProviderAccountSecret.ResolveId(accounts, null, DateTime.UtcNow);
            hasSubscription = accountId is not null;
        }
        // What was mounted, pinned or not: the limit report from this pod names this account.
        if (record.ResolvedCredentialId != accountId)
        {
            record.ResolvedCredentialId = accountId;
            await _store.SetResolvedCredentialIdAsync(record.Id, accountId, ct);
        }
        if (AgentPodSpecFactory.ResolvesApiKey(record.Mode, record.AuthMode))
        {
            var apiKey = record.Agent switch
            {
                AgentKind.Codex => "openai_api_key",
                AgentKind.Cursor => "cursor_api_key",
                AgentKind.OpenClaw => AgentPodSpecFactory.TryOpenClawApiKeySecretKey(record.OpenClawApiKeySource),
                AgentKind.OpenCode => "opencode_api_key",
                _ => "anthropic_api_key"
            };
            hasApiKey = apiKey is not null && await HasSecretKeyAsync(CredsSecretName(owner), apiKey, ct);
        }

        var ownerKey = Sanitize(owner);
        var claudeImage = string.IsNullOrWhiteSpace(_opts.ClaudeAgentImage) ? _opts.AgentImage : _opts.ClaudeAgentImage;
        var artifactUrls = AgentSessionResourceOrchestrator.PresignArtifactUrls(
            _artifacts, ownerKey, record, resume, PresignTtl);
        return new PodBuildContext
        {
            Owner = owner,
            CredentialsSecretName = CredsSecretName(owner),
            ClaudeCredentialSecretName = ProviderSecretName(owner, AgentKind.Claude),
            CodexCredentialSecretName = ProviderSecretName(owner, AgentKind.Codex),
            CursorCredentialSecretName = ProviderSecretName(owner, AgentKind.Cursor),
            OpenClawCredentialSecretName = ProviderSecretName(owner, AgentKind.OpenClaw),
            OpenCodeCredentialSecretName = ProviderSecretName(owner, AgentKind.OpenCode),
            HasSelectedApiKey = hasApiKey,
            HasSelectedSubscriptionCredential = hasSubscription,
            SubscriptionAccountId = accountId,
            HasGitCredentials = hasGitCredentials,
            CallbackUrl = $"{_callbackBaseUrl}/internal/sessions/{record.Id}",
            StatePutUrl = artifactUrls.StatePutUrl,
            StateGetUrl = artifactUrls.StateGetUrl,
            ScrollbackPutUrl = artifactUrls.ScrollbackPutUrl,
            TranscriptPutUrl = artifactUrls.TranscriptPutUrl,
            S3Insecure = _s3Insecure,
            RuntimeImages = new AgentRuntimeImages(
                claudeImage, _opts.CodexAgentImage, _opts.CursorAgentImage, _opts.OpenClawAgentImage,
                _opts.OpenCodeAgentImage, _opts.AgentImagePullPolicy),
            Runtime = new AgentPodRuntimeSettings
            {
                AgentPort = _opts.AgentPort,
                BrowserEnabled = _browserEnabled,
                SpawnMcpEnabled = _spawnMcpEnabled,
                NetworkMcpEnabled = _networkMcpEnabled,
                ContainersEnabled = _containersEnabled,
                GitCloneImage = _opts.GitCloneImage,
                ImagePullSecret = _opts.ImagePullSecret,
                RuntimeClassName = _opts.RuntimeClassName,
                MaxCpu = _opts.MaxCpu,
                MaxMemory = _opts.MaxMemory,
                TelemetryEnabled = _opts.TelemetryEnabled,
                TelemetryOtlpEndpoint = _opts.TelemetryOtlpEndpoint,
                AccountLimitThreshold = Math.Clamp(_opts.AccountLimitThreshold, 1, 100)
            }
        };
    }

    private async Task<bool> HasSecretKeyAsync(string secretName, string key, CancellationToken ct) =>
        (await ReadSecretOrNullAsync(secretName, ct))?.Data?.ContainsKey(key) == true;

    // ---------------------------------------------------------------- Helpers
    private async Task CreateMcpSecretAsync(string owner, string id, string json, CancellationToken ct) =>
        await UpsertSecretAsync(new V1Secret
        {
            Metadata = new V1ObjectMeta
            {
                Name = $"mcp-{id}", NamespaceProperty = _opts.Namespace,
                Labels = new Dictionary<string, string> { [OwnerLabel] = Sanitize(owner), [SessionLabel] = id }
            },
            Type = "Opaque", Data = new Dictionary<string, byte[]> { ["mcp.json"] = Encoding.UTF8.GetBytes(json) }
        }, ct);

    private static string EffectivePhase(SessionRecord record, V1Pod? pod)
        => SessionStartupFailure.FromPod(pod, ParseRepos(record))?.Terminal == true
            ? SessionStatus.Failed : pod?.Status?.Phase ?? record.Status;

    private async Task ObserveFailureAsync(SessionRecord record, V1Pod? pod, CancellationToken ct)
    {
        var failure = SessionStartupFailure.FromPod(pod, ParseRepos(record));
        // Keep the last diagnostic when a finished pod is collected, but discard a transient
        // image/scheduling warning once the same pod becomes healthy.
        if (pod is null) return;
        var reason = failure?.Message;
        if (reason == record.FailureReason) return;
        await _store.SetFailureReasonAsync(record.Id, reason, failure?.Terminal == true, ct);
        record.FailureReason = reason;
        if (failure?.Terminal == true) record.Status = SessionStatus.Failed;
    }

    private async Task<bool> SessionHasEphemeralMcpAsync(string sessionId, CancellationToken ct) =>
        (await _ephemeralApiMcps.ListBySessionAsync(sessionId, ct)).Count > 0;

    private async Task<SessionInfo> ToInfoAsync(
        SessionRecord r, string phase, string? podIp, BrowserSummary? browser = null, CancellationToken ct = default) =>
        ToInfo(r, phase, podIp, browser, await SessionHasEphemeralMcpAsync(r.Id, ct));

    private SessionInfo ToInfo(
        SessionRecord r, string phase, string? podIp, BrowserSummary? browser = null, bool hasEphemeralMcp = false) => new()
    {
        Url = SessionUrl.For(_frontendOrigin, r.Id), SystemPrompt = r.SystemPrompt,
        Id = r.Id, Title = r.Title, Description = r.Description, Owner = r.Owner,
        Mode = r.Mode, UiMode = r.UiMode, RepoUrl = r.RepoUrl,
        Repos = ParseRepos(r),
        HasMcp = SessionMcpConfig.HasMcp(r.McpConfigJson, ParseMcpServerIds(r), hasEphemeralMcp),
        McpConfigJson = r.McpConfigJson, McpServerIds = ParseMcpServerIds(r),
        FailureReason = string.IsNullOrEmpty(r.FailureReason) ? null : SessionStartupFailure.Redact(r.FailureReason),
        Phase = phase, PodIp = podIp, CreatedAt = r.CreatedAt, Schedule = r.Schedule,
        ProjectId = r.ProjectId, ParentSessionId = r.ParentSessionId, Prompt = r.Prompt, AllowedTools = ParsePolicy(r).AllowedTools,
        Agent = r.Agent, AuthMode = r.AuthMode, OpenClawApiKeySource = r.OpenClawApiKeySource,
        CredentialId = r.CredentialId, ResolvedCredentialId = r.ResolvedCredentialId,
        GitPatIds = GitPatSelection.Parse(r.GitPatIdsJson),
        SecretIds = SessionSecretSelection.Parse(r.SecretIdsJson),
        SecretGrantIds = SessionSecretSelection.Parse(r.SecretGrantIdsJson),
        Policy = ParsePolicy(r),
        QuestionPending = r.QuestionPending,
        AutoUpdateRuntimeImages = r.AutoUpdateRuntimeImages,
        CanResume = SessionStatus.CanResume(r.Mode, phase),
        CanConvertToInteractive = SessionStatus.CanConvertToInteractive(r.Mode, phase),
        ConvertedFrom = r.ConvertedFrom,
        Image = r.Image, RunAsRoot = r.RunAsRoot, AutoApprove = r.AutoApprove, Cpu = r.Cpu, Memory = r.Memory,
        Browser = browser ?? BrowserSummary.Stopped,
        AutoDeleteAfterSeconds = r.AutoDeleteAfterSeconds, AutoDeleteFrom = r.AutoDeleteFrom,
        ExpiresAt = SessionExpiry.ExpiresAt(r), LastActivityAt = r.LastActivityAt,
        AccountFailover = AccountFailoverMode.Display(r.AccountFailover)
    };

    private V1ObjectMeta Meta(string name, string owner, string id, string component,
        string? title = null)
    {
        var labels = new Dictionary<string, string>
        {
            [OwnerLabel] = Sanitize(owner), [SessionLabel] = id, [ComponentLabel] = component
        };
        return new V1ObjectMeta
        {
            Name = name, NamespaceProperty = _opts.Namespace, Labels = labels,
            Annotations = title is null ? null : new Dictionary<string, string>
            {
                ["agenthub.dev/title"] = title
            }
        };
    }

    private async Task<V1Pod?> TryReadPodAsync(string name, CancellationToken ct)
    {
        try { return await _k8s.CoreV1.ReadNamespacedPodAsync(name, _opts.Namespace, cancellationToken: ct); }
        catch (k8s.Autorest.HttpOperationException e) when (e.Response.StatusCode == System.Net.HttpStatusCode.NotFound) { return null; }
    }

    private async Task TryDeletePodAsync(string name, CancellationToken ct, int gracePeriodSeconds = 5)
    {
        try { await _k8s.CoreV1.DeleteNamespacedPodAsync(name, _opts.Namespace, gracePeriodSeconds: gracePeriodSeconds, cancellationToken: ct); } catch { }
    }

    private async Task<V1Secret?> ReadSecretOrNullAsync(string name, CancellationToken ct)
    {
        try { return await _k8s.CoreV1.ReadNamespacedSecretAsync(name, _opts.Namespace, cancellationToken: ct); }
        catch (k8s.Autorest.HttpOperationException e) when (e.Response.StatusCode == System.Net.HttpStatusCode.NotFound) { return null; }
    }

    private async Task UpsertSecretAsync(V1Secret secret, CancellationToken ct)
    {
        try { await _k8s.CoreV1.CreateNamespacedSecretAsync(secret, _opts.Namespace, cancellationToken: ct); }
        catch (k8s.Autorest.HttpOperationException e) when (e.Response.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            await _k8s.CoreV1.ReplaceNamespacedSecretAsync(secret, secret.Metadata.Name, _opts.Namespace, cancellationToken: ct);
        }
    }

    private static string CredsSecretName(string owner) => $"creds-{Sanitize(owner)}";
    /// <summary>The owner's secret store; never mounted, see <see cref="SessionSecretStore"/>.</summary>
    private static string UserSecretsName(string owner) => $"usersecrets-{Sanitize(owner)}";
    public static string ProviderSecretName(string owner, AgentKind agent) => agent switch
    {
        AgentKind.Claude => $"claude-{Sanitize(owner)}",
        AgentKind.Codex => $"codex-{Sanitize(owner)}",
        AgentKind.Cursor => $"cursor-{Sanitize(owner)}",
        AgentKind.OpenClaw => $"openclaw-{Sanitize(owner)}",
        AgentKind.OpenCode => $"opencode-{Sanitize(owner)}",
        _ => throw new ArgumentException("Unsupported agent kind.", nameof(agent))
    };

    private static string Sanitize(string owner)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(owner)))[..16].ToLowerInvariant();
        return $"u-{hash}";
    }

    private static string RandomToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    private static string? Normalize(string? pem) => pem is null ? null : pem.Replace("\r\n", "\n").TrimEnd() + "\n";
}

public sealed class AgentHubOptions
{
    public string Namespace { get; set; } = "agenthub-sessions";
    /// <summary>Legacy Claude image option; used when ClaudeAgentImage is unset.</summary>
    public string AgentImage { get; set; } = "";
    public string ClaudeAgentImage { get; set; } = "";
    public string CodexAgentImage { get; set; } = "";
    public string CursorAgentImage { get; set; } = "";
    public string OpenClawAgentImage { get; set; } = "";
    public string OpenCodeAgentImage { get; set; } = "";
    public int AgentPort { get; set; } = 7681;
    public string GitCloneImage { get; set; } = "alpine/git:2.45.2";
    /// <summary>Pull policy for the agent/runtime image. Set "Always" when the agent
    /// image uses a moving tag (e.g. :latest) so nodes don't serve a stale cache.</summary>
    public string AgentImagePullPolicy { get; set; } = "IfNotPresent";
    public string ImagePullSecret { get; set; } = "";
    public string RuntimeClassName { get; set; } = "";
    public string MaxCpu { get; set; } = "2";
    public string MaxMemory { get; set; } = "4Gi";
    /// <summary>Users may specify a custom container image for their session.</summary>
    public bool AllowCustomImage { get; set; } = true;
    public bool AutoUpdateRuntimeImages { get; set; }
    /// <summary>Users may run their session as root (namespace needs PSA "baseline").</summary>
    public bool AllowRootSessions { get; set; } = true;
    /// <summary>Grace period (seconds) when pausing a session, so the agent can upload
    /// its state to S3 before the container is killed.</summary>
    public int PauseGracePeriodSeconds { get; set; } = 30;
    /// <summary>Inject the built-in skill-library MCP server into every session, so the
    /// agent can search, upload and version skills. An inline server entry with the
    /// same name ("skill-library") overrides the injected one.</summary>
    public bool SkillLibraryMcp { get; set; } = true;
    /// <summary>Enable Claude Code OpenTelemetry metrics export (token/cost usage) from session pods.</summary>
    public bool TelemetryEnabled { get; set; } = true;
    /// <summary>Optional OTLP endpoint base override. Empty = derive from CallbackBaseUrl
    /// (the internal backend service); the OTEL SDK appends "/v1/metrics".</summary>
    public string TelemetryOtlpEndpoint { get; set; } = "";
    /// <summary>Max concurrent non-terminal sessions per owner (Pending|Running|Paused|Scheduled).
    /// Values &lt;= 0 are treated as the default (20).</summary>
    public int MaxRunningSessionsPerOwner { get; set; } = SessionSoftLimit.DefaultMax;
    /// <summary>Inject the in-pod agenthub_sessions MCP and allow internal spawn.</summary>
    public bool SpawnMcpEnabled { get; set; } = true;
    /// <summary>Percent of a provider rate-limit window at which a Claude session reports its
    /// account as at its limit (docs/account-limits.md). 100 reports the limit itself; lower
    /// values switch accounts early and spend a second window on work the first could still do.</summary>
    public int AccountLimitThreshold { get; set; } = 100;
}
