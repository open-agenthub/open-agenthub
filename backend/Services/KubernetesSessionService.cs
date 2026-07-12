using System.Security.Cryptography;
using System.Text;
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
    private readonly IArtifactStore _artifacts;
    private readonly ILogger<KubernetesSessionService> _log;
    private readonly AgentHubOptions _opts;
    private readonly string _callbackBaseUrl;

    private const string OwnerLabel = "agenthub.dev/owner";
    private const string SessionLabel = "agenthub.dev/session";
    private const string ComponentLabel = "agenthub.dev/component";
    private static readonly TimeSpan PresignTtl = TimeSpan.FromHours(12);

    public KubernetesSessionService(IConfiguration cfg, ISessionStore store,
        IArtifactStore artifacts, ILogger<KubernetesSessionService> log)
    {
        _log = log;
        _store = store;
        _artifacts = artifacts;
        _opts = cfg.GetSection("AgentHub").Get<AgentHubOptions>() ?? new AgentHubOptions();
        _callbackBaseUrl = cfg["AgentHub:CallbackBaseUrl"]
            ?? "http://agenthub-backend.agenthub.svc.cluster.local";

        var config = KubernetesClientConfiguration.IsInCluster()
            ? KubernetesClientConfiguration.InClusterConfig()
            : KubernetesClientConfiguration.BuildConfigFromConfigFile();
        _k8s = new Kubernetes(config);
    }

    // ---------------------------------------------------------------- Credentials

    public async Task StoreCredentialsAsync(string owner, UserCredentials c, CancellationToken ct = default)
    {
        var name = CredsSecretName(owner);
        var data = new Dictionary<string, byte[]>();
        void Put(string k, string? v) { if (!string.IsNullOrEmpty(v)) data[k] = Encoding.UTF8.GetBytes(v); }

        Put("ssh_key", Normalize(c.SshPrivateKey));
        Put("gitlab_token", c.GitlabToken);
        Put("anthropic_api_key", c.AnthropicApiKey);
        Put("known_hosts", c.GitKnownHosts);
        Put("git_user_name", c.GitUserName);
        Put("git_user_email", c.GitUserEmail);

        await UpsertSecretAsync(new V1Secret
        {
            Metadata = new V1ObjectMeta
            {
                Name = name, NamespaceProperty = _opts.Namespace,
                Labels = new Dictionary<string, string> { [OwnerLabel] = Sanitize(owner) }
            },
            Type = "Opaque", Data = data
        }, ct);
        _log.LogInformation("Stored credentials for {Owner} ({Keys} keys)", owner, data.Count);
    }

    /// <summary>
    /// Stores the Claude CLI OAuth credentials (subscription login) in a dedicated secret.
    /// Separate secret so StoreCredentialsAsync (which fully replaces its secret) does not overwrite it.
    /// Uploaded by the agent pod whenever ~/.claude/.credentials.json changes.
    /// </summary>
    public async Task StoreClaudeCredentialsAsync(string owner, string credentialsJson, CancellationToken ct = default)
    {
        await UpsertSecretAsync(new V1Secret
        {
            Metadata = new V1ObjectMeta
            {
                Name = ClaudeSecretName(owner), NamespaceProperty = _opts.Namespace,
                Labels = new Dictionary<string, string> { [OwnerLabel] = Sanitize(owner) }
            },
            Type = "Opaque",
            Data = new Dictionary<string, byte[]> { ["credentials.json"] = Encoding.UTF8.GetBytes(credentialsJson) }
        }, ct);
        _log.LogInformation("Saved Claude login for {Owner}", owner);
    }

    // ---------------------------------------------------------------- Create / Resume

    public async Task<SessionInfo> CreateSessionAsync(string owner, CreateSessionRequest req, CancellationToken ct = default)
    {
        if (req.Mode is SessionMode.Autonomous or SessionMode.Scheduled && string.IsNullOrWhiteSpace(req.Prompt))
            throw new ArgumentException("A prompt is required for Autonomous/Scheduled sessions.");

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

        var id = Guid.NewGuid().ToString("n")[..12];
        var rec = new SessionRecord
        {
            Id = id, Owner = owner, Title = req.Title, Mode = req.Mode,
            RepoUrl = req.RepoUrl, Schedule = req.Schedule,
            Image = image, RunAsRoot = req.RunAsRoot,
            ClaudeSessionId = Guid.NewGuid().ToString(),
            CallbackToken = RandomToken(),
            Status = req.Mode == SessionMode.Scheduled ? "Scheduled" : "Pending"
        };
        await _store.UpsertAsync(rec, ct);

        if (!string.IsNullOrWhiteSpace(req.McpConfigJson))
            await CreateMcpSecretAsync(owner, id, req.McpConfigJson, ct);

        await SpawnAsync(owner, rec, req, resume: false, ct);
        return ToInfo(rec, phase: rec.Status, podIp: null);
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
            Title = rec.Title, Mode = rec.Mode, RepoUrl = rec.RepoUrl,
            Image = rec.Image, RunAsRoot = rec.RunAsRoot
        };
        await SpawnAsync(owner, rec, req, resume: true, ct);
        _log.LogInformation("Resuming session {Id} (claudeSessionId={Csid})", id, rec.ClaudeSessionId);
        return ToInfo(rec, phase: "Pending", podIp: null);
    }

    private async Task SpawnAsync(string owner, SessionRecord rec, CreateSessionRequest req, bool resume, CancellationToken ct)
    {
        var podSpec = BuildPodSpec(owner, rec, req, resume);

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

    public async Task<string?> GetTranscriptAsync(string owner, string id, CancellationToken ct = default)
    {
        if (await _store.GetAsync(owner, id, ct) is null) return null;
        return await _artifacts.GetTextAsync(IArtifactStore.ScrollbackKey(Sanitize(owner), id), ct);
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
        await _store.DeleteAsync(id, ct);
        _log.LogInformation("Deleted session {Id} (S3 artifacts are kept)", id);
    }

    // ---------------------------------------------------------------- Pod-Spec

    private V1PodSpec BuildPodSpec(string owner, SessionRecord rec, CreateSessionRequest req, bool resume)
    {
        var credsSecret = CredsSecretName(owner);
        var ownerKey = Sanitize(owner);
        var hasRepo = !string.IsNullOrWhiteSpace(req.RepoUrl);
        var hasMcp = !resume && !string.IsNullOrWhiteSpace(req.McpConfigJson);
        var customImage = string.IsNullOrWhiteSpace(req.Image) ? null : req.Image;
        var asRoot = req.RunAsRoot;
        var uid = asRoot ? 0L : 1000L;
        var home = asRoot ? "/root" : "/home/agent";

        // Root sessions: unprivileged (no privileged mode, seccomp, no privilege escalation), but UID 0 with
        // default capabilities and a writable rootfs so apt & friends work.
        var podSecurity = new V1PodSecurityContext
        {
            RunAsNonRoot = !asRoot, RunAsUser = uid, RunAsGroup = uid, FsGroup = uid,
            SeccompProfile = new V1SeccompProfile { Type = "RuntimeDefault" }
        };
        V1SecurityContext ContainerSecurity() => new()
        {
            AllowPrivilegeEscalation = false, ReadOnlyRootFilesystem = !asRoot, RunAsNonRoot = !asRoot, RunAsUser = uid,
            Capabilities = asRoot ? null : new V1Capabilities { Drop = new List<string> { "ALL" } }
        };

        var volumes = new List<V1Volume>
        {
            new() { Name = "workspace", EmptyDir = new V1EmptyDirVolumeSource() },
            new() { Name = "home", EmptyDir = new V1EmptyDirVolumeSource() },
            new() { Name = "tmp", EmptyDir = new V1EmptyDirVolumeSource() },
            // Optional: sessions can start before the user ever saved credentials
            // (all consumers check file existence; the volume is just empty then).
            new() { Name = "creds", Secret = new V1SecretVolumeSource { SecretName = credsSecret, Optional = true, DefaultMode = 0x1A0 } },
            // Claude subscription login (optional; only exists after the first login)
            new() { Name = "claude", Secret = new V1SecretVolumeSource { SecretName = ClaudeSecretName(owner), Optional = true, DefaultMode = 0x1A0 } }
        };
        if (hasMcp)
            volumes.Add(new V1Volume { Name = "mcp", Secret = new V1SecretVolumeSource { SecretName = $"mcp-{rec.Id}", DefaultMode = 0x1A0 } });
        if (customImage is not null)
            volumes.Add(new V1Volume { Name = "runtime", EmptyDir = new V1EmptyDirVolumeSource() });

        var mounts = new List<V1VolumeMount>
        {
            new() { Name = "workspace", MountPath = "/workspace" },
            new() { Name = "home", MountPath = home },
            new() { Name = "tmp", MountPath = "/tmp" },
            new() { Name = "creds", MountPath = "/secrets/creds", ReadOnlyProperty = true },
            new() { Name = "claude", MountPath = "/secrets/claude", ReadOnlyProperty = true }
        };
        if (hasMcp)
            mounts.Add(new V1VolumeMount { Name = "mcp", MountPath = "/secrets/mcp", ReadOnlyProperty = true });
        if (customImage is not null)
            mounts.Add(new V1VolumeMount { Name = "runtime", MountPath = "/opt/agenthub" });

        var statePut = _artifacts.PresignPut(IArtifactStore.StateKey(ownerKey, rec.Id), PresignTtl);
        var scrollPut = _artifacts.PresignPut(IArtifactStore.ScrollbackKey(ownerKey, rec.Id), PresignTtl);
        var stateGet = resume ? _artifacts.PresignGet(IArtifactStore.StateKey(ownerKey, rec.Id), PresignTtl) : "";

        var env = new List<V1EnvVar>
        {
            new() { Name = "HOME", Value = home },
            new() { Name = "AGENTHUB_MODE", Value = req.Mode.ToString().ToLowerInvariant() },
            new() { Name = "AGENTHUB_SESSION_ID", Value = rec.Id },
            new() { Name = "AGENTHUB_CLAUDE_SESSION_ID", Value = rec.ClaudeSessionId },
            new() { Name = "AGENTHUB_PORT", Value = _opts.AgentPort.ToString() },
            new() { Name = "AGENTHUB_HAS_REPO", Value = hasRepo ? "1" : "0" },
            new() { Name = "AGENTHUB_HAS_MCP", Value = hasMcp ? "1" : "0" },
            new() { Name = "AGENTHUB_RESUME", Value = resume ? "1" : "0" },
            new() { Name = "AGENTHUB_PROMPT", Value = req.Prompt ?? "" },
            new() { Name = "AGENTHUB_ALLOWED_TOOLS", Value = string.Join(",", req.AllowedTools) },
            new() { Name = "AGENTHUB_CALLBACK_URL", Value = $"{_callbackBaseUrl}/internal/sessions/{rec.Id}" },
            new() { Name = "AGENTHUB_CALLBACK_TOKEN", Value = rec.CallbackToken },
            new() { Name = "AGENTHUB_STATE_PUT_URL", Value = statePut },
            new() { Name = "AGENTHUB_STATE_GET_URL", Value = stateGet },
            new() { Name = "AGENTHUB_SCROLLBACK_PUT_URL", Value = scrollPut },
            new()
            {
                Name = "ANTHROPIC_API_KEY",
                ValueFrom = new V1EnvVarSource { SecretKeyRef = new V1SecretKeySelector { Name = credsSecret, Key = "anthropic_api_key", Optional = true } }
            }
        };

        var initContainers = new List<V1Container>();

        // Custom image: copy the session-agent, Node runtime, and Claude CLI from the default
        // image into an emptyDir so they are available inside the foreign image.
        if (customImage is not null)
        {
            const string copyScript = """
                set -e
                mkdir -p /opt/agenthub/bin /opt/agenthub/lib
                cp -r /opt/session-agent /opt/agenthub/session-agent
                cp /usr/local/bin/node /opt/agenthub/bin/node
                cp -r /usr/local/lib/node_modules /opt/agenthub/lib/node_modules
                cp /usr/local/bin/entrypoint.sh /opt/agenthub/entrypoint.sh
                # claude launcher: resolve the symlink target of the global npm install and link it
                target=$(readlink -f /usr/local/bin/claude)
                ln -sf "/opt/agenthub/${target#/usr/local/}" /opt/agenthub/bin/claude
                chmod -R a+rX /opt/agenthub
                chmod +x /opt/agenthub/bin/node /opt/agenthub/entrypoint.sh "$(readlink -f /opt/agenthub/bin/claude)"
                echo "Runtime copied to /opt/agenthub."
                """;
            initContainers.Add(new V1Container
            {
                Name = "copy-runtime", Image = _opts.AgentImage, ImagePullPolicy = "IfNotPresent",
                Command = new List<string> { "/bin/sh", "-c", copyScript },
                VolumeMounts = new List<V1VolumeMount> { new() { Name = "runtime", MountPath = "/opt/agenthub" } },
                SecurityContext = ContainerSecurity()
            });
        }

        if (hasRepo && !resume)
        {
            var cloneScript = """
                set -e
                if [ -f /secrets/creds/ssh_key ]; then
                  cp /secrets/creds/ssh_key /tmp/id && chmod 600 /tmp/id
                  export GIT_SSH_COMMAND="ssh -i /tmp/id -o IdentitiesOnly=yes -o UserKnownHostsFile=/secrets/creds/known_hosts -o StrictHostKeyChecking=yes"
                fi
                echo "Cloning $REPO_URL (branch: ${REPO_BRANCH:-default}) ..."
                if [ -n "$REPO_BRANCH" ]; then git clone --branch "$REPO_BRANCH" "$REPO_URL" /workspace/repo
                else git clone "$REPO_URL" /workspace/repo; fi
                echo "Clone finished."
                """;
            initContainers.Add(new V1Container
            {
                Name = "git-clone", Image = _opts.GitCloneImage,
                Command = new List<string> { "/bin/sh", "-c", cloneScript },
                Env = new List<V1EnvVar>
                {
                    new() { Name = "REPO_URL", Value = req.RepoUrl },
                    new() { Name = "REPO_BRANCH", Value = req.RepoBranch ?? "" },
                    new() { Name = "HOME", Value = home }
                },
                VolumeMounts = mounts, SecurityContext = ContainerSecurity()
            });
        }

        var agent = new V1Container
        {
            Name = "agent", Image = customImage ?? _opts.AgentImage, ImagePullPolicy = "IfNotPresent",
            // In a foreign image the entrypoint lives in the copied runtime volume (requires bash in the image).
            Command = customImage is null ? null : new List<string> { "/bin/bash", "/opt/agenthub/entrypoint.sh" },
            Ports = new List<V1ContainerPort> { new() { ContainerPort = _opts.AgentPort, Name = "term" } },
            Env = env, VolumeMounts = mounts, SecurityContext = ContainerSecurity(),
            Resources = new V1ResourceRequirements
            {
                Requests = new Dictionary<string, ResourceQuantity> { ["cpu"] = new(req.Cpu), ["memory"] = new(req.Memory) },
                Limits = new Dictionary<string, ResourceQuantity> { ["cpu"] = new(_opts.MaxCpu), ["memory"] = new(_opts.MaxMemory) }
            },
            ReadinessProbe = new V1Probe { TcpSocket = new V1TCPSocketAction { Port = _opts.AgentPort }, InitialDelaySeconds = 5, PeriodSeconds = 10 }
        };

        return new V1PodSpec
        {
            RestartPolicy = "Never",
            AutomountServiceAccountToken = false,
            ServiceAccountName = "agenthub-agent",
            SecurityContext = podSecurity,
            EnableServiceLinks = false,
            ImagePullSecrets = string.IsNullOrEmpty(_opts.ImagePullSecret) ? null : new List<V1LocalObjectReference> { new() { Name = _opts.ImagePullSecret } },
            InitContainers = initContainers.Count > 0 ? initContainers : null,
            Containers = new List<V1Container> { agent },
            Volumes = volumes,
            RuntimeClassName = string.IsNullOrEmpty(_opts.RuntimeClassName) ? null : _opts.RuntimeClassName
        };
    }

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
        Phase = phase, PodIp = podIp, CreatedAt = r.CreatedAt, Schedule = r.Schedule,
        QuestionPending = r.QuestionPending,
        CanResume = r.Mode != SessionMode.Scheduled && (phase == "Succeeded" || phase == "Failed"),
        Image = r.Image, RunAsRoot = r.RunAsRoot
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

    private async Task TryDeletePodAsync(string name, CancellationToken ct)
    {
        try { await _k8s.CoreV1.DeleteNamespacedPodAsync(name, _opts.Namespace, gracePeriodSeconds: 5, cancellationToken: ct); } catch { }
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
    private static string ClaudeSecretName(string owner) => $"claude-{Sanitize(owner)}";

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
    public string AgentImage { get; set; } = "";
    public int AgentPort { get; set; } = 7681;
    public string GitCloneImage { get; set; } = "alpine/git:2.45.2";
    public string ImagePullSecret { get; set; } = "";
    public string RuntimeClassName { get; set; } = "";
    public string MaxCpu { get; set; } = "2";
    public string MaxMemory { get; set; } = "4Gi";
    /// <summary>Users may specify a custom container image for their session.</summary>
    public bool AllowCustomImage { get; set; } = true;
    /// <summary>Users may run their session as root (namespace needs PSA "baseline").</summary>
    public bool AllowRootSessions { get; set; } = true;
}
