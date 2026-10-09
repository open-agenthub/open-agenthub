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
public sealed class KubernetesSessionService : ISessionService
{
    private readonly Kubernetes _k8s;
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
    private readonly ILogger<KubernetesSessionService> _log;
    private readonly AgentHubOptions _opts;
    private readonly string _callbackBaseUrl;
    private readonly string _mcpGatewayBaseUrl;
    private readonly bool _s3Insecure;
    private readonly bool _browserEnabled;
    private readonly bool _spawnMcpEnabled;
    private readonly bool _networkMcpEnabled;
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
        ISessionFileCleanup? fileCleanup = null, Network.INetworkSessionCleanup? networkCleanup = null)
    {
        _log = log;
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
    public async Task StoreProviderCredentialsAsync(string owner, AgentKind agent, string json, CancellationToken ct = default)
    {
        var secret = CredentialSecretFactory.CreateProviderSecret(ProviderSecretName(owner, agent), _opts.Namespace, Sanitize(owner), agent, json);
        await UpsertSecretAsync(secret, ct);
        _log.LogInformation("Saved {Agent} login for {Owner}", agent, owner);
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
            AgentPolicyJson = SerializePolicy(policy),
            AllowedToolsJson = SerializeAllowedTools(policy.AllowedTools),
            Image = image, RunAsRoot = req.RunAsRoot,
            AutoApprove = CreateSessionRequest.AutoApproveFor(req.AutoApprove, req.Mode),
            Cpu = req.Cpu, Memory = req.Memory,
            AgentSessionId = Guid.NewGuid().ToString(),
            CallbackToken = RandomToken(),
            Status = req.Mode == SessionMode.Scheduled ? "Scheduled" : "Pending"
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

    public async Task<SessionInfo> ResumeSessionAsync(string owner, string id, CancellationToken ct = default)
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
        rec.QuestionPending = false;
        await _store.UpsertAsync(rec, ct);

        var req = new CreateSessionRequest
        {
            Title = rec.Title, Description = rec.Description, Mode = rec.Mode, UiMode = rec.UiMode,
            Repos = ParseRepos(rec), McpConfigJson = rec.McpConfigJson,
            McpServerIds = ParseMcpServerIds(rec),
            ProjectId = rec.ProjectId, Prompt = rec.Prompt, SystemPrompt = rec.SystemPrompt,
            Agent = rec.Agent, AuthMode = rec.AuthMode, OpenClawApiKeySource = rec.OpenClawApiKeySource,
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
    /// Pauses a session: deleting the pod sends SIGTERM, whereupon the agent uploads
    /// its Claude state + scrollback to S3 during the grace period (see server.js).
    /// The session is marked "Paused" and can later be resumed from that saved state.
    /// </summary>
    public async Task<SessionInfo> PauseSessionAsync(string owner, string id, CancellationToken ct = default)
    {
        var rec = await _store.GetAsync(owner, id, ct)
            ?? throw new KeyNotFoundException($"Session {id} not found.");
        if (rec.Mode == SessionMode.Scheduled)
            throw new ArgumentException("Scheduled sessions cannot be paused; they run on their schedule.");

        await _browsers.StopAsync(id, ct);

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
        var rec = await _store.GetAsync(owner, id, ct)
            ?? throw new KeyNotFoundException($"Session {id} not found.");
        SessionUpdateValidator.Validate(rec, req);
        if (req.Agent is { } requestedAgent)
            await EnsureAgentAllowedAsync(requestedAgent, ct);

        if (!string.IsNullOrWhiteSpace(req.Title))
            rec.Title = req.Title.Trim();
        // null = unchanged; an empty string clears the description.
        if (req.Description is not null)
            rec.Description = SessionDescription.Normalize(req.Description);
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
        if (req.Policy is { } policy)
        {
            rec.AgentPolicyJson = SerializePolicy(policy);
            rec.AllowedToolsJson = SerializeAllowedTools(policy.AllowedTools);
        }

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

        var pod = await TryReadPodAsync($"session-{id}", ct);
        return await ToInfoAsync(rec, pod?.Status?.Phase ?? rec.Status, pod?.Status?.PodIP,
            await _browsers.GetSummaryAsync(id, ct), ct);
    }

    private static void ValidateQuantity(string value, string what)
    {
        try { _ = new ResourceQuantity(value).ToDecimal(); }
        catch { throw new ArgumentException($"Invalid {what} quantity: '{value}'."); }
    }

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
                await _store.UpsertAsync(rec, resourceCt);
                await _store.SetScrollbackAsync(rec.Id, diagnostic, resourceCt);
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
                var oauthStore = await _gitAuth.BuildCredentialStoreAsync(
                    owner, NormalizeRepos(req), resourceCt);
                var manualCredentials = (await ReadSecretOrNullAsync(
                    CredsSecretName(owner), resourceCt))?.Data;
                var credentialStore = ManualGitCredentials.ComposeStore(
                    oauthStore, ManualGitCredentials.Lines(manualCredentials));
                if (credentialStore is null) return false;

                await UpsertSecretAsync(new V1Secret
                {
                    Metadata = new V1ObjectMeta
                    {
                        Name = $"gitcreds-{rec.Id}", NamespaceProperty = _opts.Namespace,
                        Labels = new Dictionary<string, string>
                        {
                            [OwnerLabel] = Sanitize(owner), [SessionLabel] = rec.Id
                        }
                    },
                    Type = "Opaque",
                    Data = new Dictionary<string, byte[]>
                    {
                        ["credentials"] = Encoding.UTF8.GetBytes(credentialStore)
                    }
                }, resourceCt);
                return true;
            },
            ct);
        if (!preparation.ShouldSpawn) return;

        context = context with { HasGitCredentials = preparation.HasGitCredentials };
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
            await _k8s.CoreV1.CreateNamespacedPodAsync(new V1Pod
            {
                Metadata = Meta($"session-{rec.Id}", owner, rec.Id, "agent", rec.Title),
                Spec = podSpec
            }, _opts.Namespace, cancellationToken: ct);
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

        var browserSummaries = await _browsers.GetSummariesAsync(
            records.Select(record => record.Id).ToArray(), ct);
        var withEphemeralMcp = await _ephemeralApiMcps.ListSessionsWithEntriesAsync(
            records.Select(record => record.Id).ToArray(), ct);
        return records.Select(r =>
        {
            byId.TryGetValue(r.Id, out var pod);
            return ToInfo(r, pod?.Status?.Phase ?? r.Status, pod?.Status?.PodIP, browserSummaries[r.Id],
                withEphemeralMcp.Contains(r.Id));
        }).ToList();
    }

    public async Task<SessionInfo?> GetSessionAsync(string owner, string id, CancellationToken ct = default)
    {
        var rec = await _store.GetAsync(owner, id, ct);
        if (rec is null) return null;
        var pod = await TryReadPodAsync($"session-{id}", ct);
        return await ToInfoAsync(rec, pod?.Status?.Phase ?? rec.Status, pod?.Status?.PodIP,
            await _browsers.GetSummaryAsync(id, ct), ct);
    }

    public async Task ClearQuestionAsync(string owner, string id, CancellationToken ct = default)
    {
        if (await _store.GetAsync(owner, id, ct) is not null)
            await _store.SetQuestionPendingAsync(id, false, ct);
    }

    public async Task<string?> GetTranscriptAsync(string owner, string id, CancellationToken ct = default)
    {
        if (await _store.GetAsync(owner, id, ct) is null) return null;
        // Prefer S3 (survives DB trimming); fall back to the Postgres-stored
        // scrollback so transcripts work on instances without S3.
        var key = IArtifactStore.ScrollbackKey(Sanitize(owner), id);
        return await TranscriptReader.ReadAsync(
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

    public async Task DeleteSessionAsync(string owner, string id, CancellationToken ct = default)
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
        await TryDeletePodAsync($"session-{id}", ct);
        try { await _k8s.BatchV1.DeleteNamespacedCronJobAsync($"session-{id}", _opts.Namespace, propagationPolicy: "Foreground", cancellationToken: ct); } catch { }
        try { await _k8s.CoreV1.DeleteNamespacedSecretAsync($"mcp-{id}", _opts.Namespace, cancellationToken: ct); } catch { }
        try { await _k8s.CoreV1.DeleteNamespacedSecretAsync($"gitcreds-{id}", _opts.Namespace, cancellationToken: ct); } catch { }
        await _ephemeralApiMcps.DeleteBySessionAsync(id, ct);
        await _store.DeleteAsync(id, ct);
        _log.LogInformation("Deleted session {Id}", id);
    }

    /// <summary>
    /// Deletes every secret labeled with the owner (creds-*, provider logins, gitauth-*,
    /// plus any leftover per-session secrets) — the Kubernetes part of an account purge.
    /// </summary>
    public async Task DeleteUserSecretsAsync(string owner, CancellationToken ct = default)
    {
        await _k8s.CoreV1.DeleteCollectionNamespacedSecretAsync(_opts.Namespace,
            labelSelector: $"{OwnerLabel}={Sanitize(owner)}", cancellationToken: ct);
        _log.LogInformation("Deleted per-user secrets for an account purge");
    }

    /// <summary>Stable, non-reversible owner key used in labels and S3 key prefixes.</summary>
    public static string OwnerKey(string owner) => Sanitize(owner);

    // ---------------------------------------------------------------- Pod-Spec

    private async Task<PodBuildContext> BuildPodContextAsync(
        string owner, SessionRecord record, bool resume, bool hasGitCredentials, CancellationToken ct)
    {
        var hasApiKey = false;
        var hasSubscription = false;
        if (record.Mode is SessionMode.Autonomous or SessionMode.Scheduled)
        {
            var (apiKey, providerKey) = record.Agent switch
            {
                AgentKind.Codex => ("openai_api_key", "auth.json"),
                AgentKind.Cursor => ("cursor_api_key", "auth.json"),
                AgentKind.OpenClaw => (AgentPodSpecFactory.TryOpenClawApiKeySecretKey(record.OpenClawApiKeySource), "auth-profiles.json"),
                AgentKind.OpenCode => ("opencode_api_key", "auth.json"),
                _ => ("anthropic_api_key", "credentials.json")
            };
            if (record.AuthMode is AgentAuthMode.ApiKey or AgentAuthMode.Auto)
                hasApiKey = apiKey is not null && await HasSecretKeyAsync(CredsSecretName(owner), apiKey, ct);
            if (record.AuthMode is AgentAuthMode.Subscription or AgentAuthMode.Auto)
                hasSubscription = await HasSecretKeyAsync(ProviderSecretName(owner, record.Agent), providerKey, ct);
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
            HasGitCredentials = hasGitCredentials,
            CallbackUrl = $"{_callbackBaseUrl}/internal/sessions/{record.Id}",
            StatePutUrl = artifactUrls.StatePutUrl,
            StateGetUrl = artifactUrls.StateGetUrl,
            ScrollbackPutUrl = artifactUrls.ScrollbackPutUrl,
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
                GitCloneImage = _opts.GitCloneImage,
                ImagePullSecret = _opts.ImagePullSecret,
                RuntimeClassName = _opts.RuntimeClassName,
                MaxCpu = _opts.MaxCpu,
                MaxMemory = _opts.MaxMemory,
                TelemetryEnabled = _opts.TelemetryEnabled,
                TelemetryOtlpEndpoint = _opts.TelemetryOtlpEndpoint
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
        Phase = phase, PodIp = podIp, CreatedAt = r.CreatedAt, Schedule = r.Schedule,
        ProjectId = r.ProjectId, ParentSessionId = r.ParentSessionId, Prompt = r.Prompt, AllowedTools = ParsePolicy(r).AllowedTools,
        Agent = r.Agent, AuthMode = r.AuthMode, OpenClawApiKeySource = r.OpenClawApiKeySource,
        Policy = ParsePolicy(r),
        QuestionPending = r.QuestionPending,
        CanResume = SessionStatus.CanResume(r.Mode, phase),
        Image = r.Image, RunAsRoot = r.RunAsRoot, AutoApprove = r.AutoApprove, Cpu = r.Cpu, Memory = r.Memory,
        Browser = browser ?? BrowserSummary.Stopped
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
}
