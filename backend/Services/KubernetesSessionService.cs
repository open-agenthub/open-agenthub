using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentHub.Api.Models;
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
    private readonly IGitAuthService _gitAuth;
    private readonly ILogger<KubernetesSessionService> _log;
    private readonly AgentHubOptions _opts;
    private readonly string _callbackBaseUrl;
    private readonly bool _s3Insecure;

    private const string OwnerLabel = "agenthub.dev/owner";
    private const string SessionLabel = "agenthub.dev/session";
    private const string ComponentLabel = "agenthub.dev/component";
    private static readonly TimeSpan PresignTtl = TimeSpan.FromHours(12);

    public KubernetesSessionService(IConfiguration cfg, ISessionStore store, IProjectStore projects,
        IArtifactStore artifacts, IGitAuthService gitAuth, ILogger<KubernetesSessionService> log)
    {
        _log = log;
        _store = store;
        _projects = projects;
        _artifacts = artifacts;
        _gitAuth = gitAuth;
        _opts = cfg.GetSection("AgentHub").Get<AgentHubOptions>() ?? new AgentHubOptions();
        _callbackBaseUrl = cfg["AgentHub:CallbackBaseUrl"]
            ?? "http://agenthub-backend.agenthub.svc.cluster.local";
        _s3Insecure = cfg.GetValue("S3:InsecureTls", false);

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
        return CredentialSecretFactory.CredentialStatus(data, claude, codex);
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

    // ---------------------------------------------------------------- Create / Resume

    public Task<SessionInfo> CreateSessionAsync(string owner, CreateSessionRequest req, CancellationToken ct = default)
        => CreateSessionCoreAsync(owner, req, allowMigratedClaudeAuto: false, ct);

    private async Task<SessionInfo> CreateSessionCoreAsync(string owner, CreateSessionRequest req, bool allowMigratedClaudeAuto, CancellationToken ct)
    {
        if (req.Mode is SessionMode.Autonomous or SessionMode.Scheduled && string.IsNullOrWhiteSpace(req.Prompt))
            throw new ArgumentException("A prompt is required for Autonomous/Scheduled sessions.");
        if (allowMigratedClaudeAuto)
            AgentConfiguration.ValidateForDuplicatedSession(req.Agent, req.AuthMode);
        else
            AgentConfiguration.ValidateForCreate(req.Agent, req.AuthMode);

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

        var repos = NormalizeRepos(req);
        var mcp = string.IsNullOrWhiteSpace(req.McpConfigJson) ? null : req.McpConfigJson;
        var policy = EffectivePolicy(req.Policy, req.AllowedTools);

        var id = Guid.NewGuid().ToString("n")[..12];
        var rec = new SessionRecord
        {
            Id = id, Owner = owner, Title = req.Title, Mode = req.Mode,
            RepoUrl = repos.FirstOrDefault()?.Url, ReposJson = SerializeRepos(repos),
            Schedule = req.Schedule, McpConfigJson = mcp,
            ProjectId = req.ProjectId, Prompt = req.Prompt,
            Agent = req.Agent, AuthMode = req.AuthMode,
            AgentPolicyJson = SerializePolicy(policy),
            AllowedToolsJson = SerializeAllowedTools(policy.AllowedTools),
            Image = image, RunAsRoot = req.RunAsRoot,
            Cpu = req.Cpu, Memory = req.Memory,
            AgentSessionId = Guid.NewGuid().ToString(),
            CallbackToken = RandomToken(),
            Status = req.Mode == SessionMode.Scheduled ? "Scheduled" : "Pending"
        };
        await _store.UpsertAsync(rec, ct);

        await SpawnAsync(owner, rec, req, resume: false, ct);
        return ToInfo(rec, phase: rec.Status, podIp: null);
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

    private async Task ValidateProjectAsync(string owner, string? projectId, CancellationToken ct)
    {
        if (projectId is not null && await _projects.GetAsync(owner, projectId, ct) is null)
            throw new ArgumentException("Project not found.");
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

        await TryDeletePodAsync($"session-{id}", ct);

        rec.Status = "Pending";
        rec.QuestionPending = false;
        await _store.UpsertAsync(rec, ct);

        var req = new CreateSessionRequest
        {
            Title = rec.Title, Mode = rec.Mode,
            Repos = ParseRepos(rec), McpConfigJson = rec.McpConfigJson,
            ProjectId = rec.ProjectId, Prompt = rec.Prompt,
            Agent = rec.Agent, AuthMode = rec.AuthMode, Policy = ParsePolicy(rec),
            AllowedTools = ParseAllowedTools(rec),
            Image = rec.Image, RunAsRoot = rec.RunAsRoot,
            Cpu = rec.Cpu, Memory = rec.Memory
        };
        await SpawnAsync(owner, rec, req, resume: true, ct);
        _log.LogInformation("Resuming session {Id} (claudeSessionId={Csid})", id, rec.AgentSessionId);
        return ToInfo(rec, phase: rec.Status, podIp: null);
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

        // Longer grace than a plain delete so the graceful state upload can finish
        // before the container is killed (the k8s default of 30s is plenty; the
        // agent uploads state, then exits).
        await TryDeletePodAsync($"session-{id}", ct, _opts.PauseGracePeriodSeconds);

        rec.Status = SessionStatus.Paused;
        rec.QuestionPending = false;
        await _store.UpsertAsync(rec, ct);
        _log.LogInformation("Paused session {Id} (pod removed, state uploaded to S3)", id);
        return ToInfo(rec, phase: SessionStatus.Paused, podIp: null);
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

        if (!string.IsNullOrWhiteSpace(req.Title))
            rec.Title = req.Title.Trim();
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
        if (req.Cpu is not null) { ValidateQuantity(req.Cpu, "cpu"); rec.Cpu = req.Cpu; }
        if (req.Memory is not null) { ValidateQuantity(req.Memory, "memory"); rec.Memory = req.Memory; }
        if (req.Repos is not null)
        {
            var repos = req.Repos.Where(r => !string.IsNullOrWhiteSpace(r.Url)).ToList();
            rec.ReposJson = SerializeRepos(repos);
            rec.RepoUrl = repos.FirstOrDefault()?.Url;
        }
        if (req.McpConfigJson is not null)
        {
            // Empty string clears the MCP config; otherwise validate and replace.
            var mcp = string.IsNullOrWhiteSpace(req.McpConfigJson) ? null : req.McpConfigJson;
            if (mcp is not null)
            {
                try { _ = JsonDocument.Parse(mcp); }
                catch { throw new ArgumentException("MCP config is not valid JSON."); }
                await CreateMcpSecretAsync(owner, id, mcp, ct);
            }
            else
            {
                try { await _k8s.CoreV1.DeleteNamespacedSecretAsync($"mcp-{id}", _opts.Namespace, cancellationToken: ct); } catch { }
            }
            rec.McpConfigJson = mcp;
        }
        if (req.ProjectIdSpecified)
        {
            await ValidateProjectAsync(owner, req.ProjectId, ct);
            rec.ProjectId = req.ProjectId;
        }
        if (req.Agent is { } agent)
            rec.Agent = agent;
        if (req.AuthMode is { } authMode)
            rec.AuthMode = authMode;
        if (req.Policy is { } policy)
        {
            rec.AgentPolicyJson = SerializePolicy(policy);
            rec.AllowedToolsJson = SerializeAllowedTools(policy.AllowedTools);
        }

        await _store.UpsertAsync(rec, ct);
        _log.LogInformation("Updated session {Id} settings", id);

        var pod = await TryReadPodAsync($"session-{id}", ct);
        return ToInfo(rec, pod?.Status?.Phase ?? rec.Status, pod?.Status?.PodIP);
    }

    private static void ValidateQuantity(string value, string what)
    {
        try { _ = new ResourceQuantity(value).ToDecimal(); }
        catch { throw new ArgumentException($"Invalid {what} quantity: '{value}'."); }
    }

    private async Task SpawnAsync(string owner, SessionRecord rec, CreateSessionRequest req, bool resume, CancellationToken ct)
    {
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
                if (!resume && !string.IsNullOrWhiteSpace(rec.McpConfigJson))
                    await CreateMcpSecretAsync(owner, rec.Id, rec.McpConfigJson, resourceCt);

                // Connected Git-provider credentials are session-scoped and must only be
                // materialized after credential preflight succeeds.
                var credentialStore = await _gitAuth.BuildCredentialStoreAsync(
                    owner, NormalizeRepos(req), resourceCt);
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
        var pods = await _k8s.CoreV1.ListNamespacedPodAsync(_opts.Namespace,
            labelSelector: $"{OwnerLabel}={Sanitize(owner)},{ComponentLabel}=agent", cancellationToken: ct);
        var byId = new Dictionary<string, V1Pod>();
        foreach (var p in pods.Items)
            if (p.Metadata.Labels is { } labels && labels.TryGetValue(SessionLabel, out var sid))
                byId[sid] = p;

        return records.Select(r =>
        {
            byId.TryGetValue(r.Id, out var pod);
            return ToInfo(r, pod?.Status?.Phase ?? r.Status, pod?.Status?.PodIP);
        }).ToList();
    }

    public async Task<SessionInfo?> GetSessionAsync(string owner, string id, CancellationToken ct = default)
    {
        var rec = await _store.GetAsync(owner, id, ct);
        if (rec is null) return null;
        var pod = await TryReadPodAsync($"session-{id}", ct);
        return ToInfo(rec, pod?.Status?.Phase ?? rec.Status, pod?.Status?.PodIP);
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
        var fromS3 = await _artifacts.GetTextAsync(IArtifactStore.ScrollbackKey(Sanitize(owner), id), ct);
        return !string.IsNullOrEmpty(fromS3) ? fromS3 : await _store.GetScrollbackAsync(id, ct);
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
        if (await _store.GetAsync(owner, id, ct) is null)
            throw new KeyNotFoundException($"Session {id} not found.");
        await TryDeletePodAsync($"session-{id}", ct);
        try { await _k8s.BatchV1.DeleteNamespacedCronJobAsync($"session-{id}", _opts.Namespace, propagationPolicy: "Foreground", cancellationToken: ct); } catch { }
        try { await _k8s.CoreV1.DeleteNamespacedSecretAsync($"mcp-{id}", _opts.Namespace, cancellationToken: ct); } catch { }
        try { await _k8s.CoreV1.DeleteNamespacedSecretAsync($"gitcreds-{id}", _opts.Namespace, cancellationToken: ct); } catch { }
        await _store.DeleteAsync(id, ct);
        _log.LogInformation("Deleted session {Id} (S3 artifacts are kept)", id);
    }

    // ---------------------------------------------------------------- Pod-Spec

    private async Task<PodBuildContext> BuildPodContextAsync(
        string owner, SessionRecord record, bool resume, bool hasGitCredentials, CancellationToken ct)
    {
        var hasApiKey = false;
        var hasSubscription = false;
        if (record.Mode is SessionMode.Autonomous or SessionMode.Scheduled)
        {
            var apiKey = record.Agent == AgentKind.Codex ? "openai_api_key" : "anthropic_api_key";
            var providerKey = record.Agent == AgentKind.Codex ? "auth.json" : "credentials.json";
            if (record.AuthMode is AgentAuthMode.ApiKey or AgentAuthMode.Auto)
                hasApiKey = await HasSecretKeyAsync(CredsSecretName(owner), apiKey, ct);
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
            HasSelectedApiKey = hasApiKey,
            HasSelectedSubscriptionCredential = hasSubscription,
            HasGitCredentials = hasGitCredentials,
            CallbackUrl = $"{_callbackBaseUrl}/internal/sessions/{record.Id}",
            StatePutUrl = artifactUrls.StatePutUrl,
            StateGetUrl = artifactUrls.StateGetUrl,
            ScrollbackPutUrl = artifactUrls.ScrollbackPutUrl,
            S3Insecure = _s3Insecure,
            RuntimeImages = new AgentRuntimeImages(claudeImage, _opts.CodexAgentImage, _opts.AgentImagePullPolicy),
            Runtime = new AgentPodRuntimeSettings
            {
                AgentPort = _opts.AgentPort,
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

    private static SessionInfo ToInfo(SessionRecord r, string phase, string? podIp) => new()
    {
        Id = r.Id, Title = r.Title, Owner = r.Owner, Mode = r.Mode, RepoUrl = r.RepoUrl,
        Repos = ParseRepos(r),
        HasMcp = !string.IsNullOrWhiteSpace(r.McpConfigJson), McpConfigJson = r.McpConfigJson,
        Phase = phase, PodIp = podIp, CreatedAt = r.CreatedAt, Schedule = r.Schedule,
        ProjectId = r.ProjectId, Prompt = r.Prompt, AllowedTools = ParsePolicy(r).AllowedTools,
        Agent = r.Agent, AuthMode = r.AuthMode, Policy = ParsePolicy(r),
        QuestionPending = r.QuestionPending,
        CanResume = SessionStatus.CanResume(r.Mode, phase),
        Image = r.Image, RunAsRoot = r.RunAsRoot, Cpu = r.Cpu, Memory = r.Memory
    };

    private V1ObjectMeta Meta(string name, string owner, string id, string component, string? title = null) => new()
    {
        Name = name, NamespaceProperty = _opts.Namespace,
        Labels = new Dictionary<string, string>
        {
            [OwnerLabel] = Sanitize(owner), [SessionLabel] = id, [ComponentLabel] = component
        },
        Annotations = title is null ? null : new Dictionary<string, string> { ["agenthub.dev/title"] = title }
    };

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
    /// <summary>Enable Claude Code OpenTelemetry metrics export (token/cost usage) from session pods.</summary>
    public bool TelemetryEnabled { get; set; }
    /// <summary>Optional OTLP endpoint base override. Empty = derive from CallbackBaseUrl
    /// (the internal backend service); the OTEL SDK appends "/v1/metrics".</summary>
    public string TelemetryOtlpEndpoint { get; set; } = "";
}
