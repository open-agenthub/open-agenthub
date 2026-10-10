using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using k8s;
using k8s.Models;

namespace AgentHub.Api.Services;

public sealed record AgentRuntimeImages(
    string ClaudeImage, string CodexImage, string CursorImage, string OpenClawImage, string PullPolicy);

public sealed record AgentPodRuntimeSettings
{
    public int AgentPort { get; init; } = 7681;
    public string GitCloneImage { get; init; } = "alpine/git:2.45.2";
    public string ImagePullSecret { get; init; } = "";
    public string RuntimeClassName { get; init; } = "";
    public string MaxCpu { get; init; } = "2";
    public string MaxMemory { get; init; } = "4Gi";
    public bool BrowserEnabled { get; init; }
    public bool SpawnMcpEnabled { get; init; }
    public bool NetworkMcpEnabled { get; init; }
    public bool TelemetryEnabled { get; init; }
    public string TelemetryOtlpEndpoint { get; init; } = "";
}

/// <summary>Non-secret inputs used to construct an agent pod.</summary>
public sealed record PodBuildContext
{
    public required string Owner { get; init; }
    public required string CredentialsSecretName { get; init; }
    public required string ClaudeCredentialSecretName { get; init; }
    public required string CodexCredentialSecretName { get; init; }
    public required string CursorCredentialSecretName { get; init; }
    public required string OpenClawCredentialSecretName { get; init; }
    public bool HasSelectedApiKey { get; init; }
    public bool HasSelectedSubscriptionCredential { get; init; }
    /// <summary>
    /// The account whose file the provider secret volume projects, or null for "no account could
    /// be resolved". The volume then projects a key that does not exist — the mount comes up empty
    /// and the entrypoint runs its in-session login path, exactly as for a user with no login.
    /// </summary>
    public string? SubscriptionAccountId { get; init; }
    public bool HasGitCredentials { get; init; }
    public required string CallbackUrl { get; init; }
    public required string StatePutUrl { get; init; }
    public required string StateGetUrl { get; init; }
    public required string ScrollbackPutUrl { get; init; }
    public required string TranscriptPutUrl { get; init; }
    public bool S3Insecure { get; init; }
    public required AgentRuntimeImages RuntimeImages { get; init; }
    public AgentPodRuntimeSettings Runtime { get; init; } = new();
}

/// <summary>Pure construction of provider- and authentication-specific pod specs.</summary>
public static class AgentPodSpecFactory
{
    public readonly record struct OpenClawApiKeyEnvBinding(string EnvName, string SecretKey);

    /// <summary>Maps a selected OpenClaw API key source to the shared creds secret key, or null when unset.</summary>
    public static string? TryOpenClawApiKeySecretKey(OpenClawApiKeySource? source) => source switch
    {
        OpenClawApiKeySource.Anthropic => "anthropic_api_key",
        OpenClawApiKeySource.OpenAI => "openai_api_key",
        OpenClawApiKeySource.Cursor => "cursor_api_key",
        null => null,
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Unknown OpenClaw API key source.")
    };

    /// <summary>Env + secret key for an OpenClaw ApiKey session. Throws when source is missing.</summary>
    public static OpenClawApiKeyEnvBinding OpenClawApiKeyBinding(OpenClawApiKeySource? source)
    {
        var secretKey = TryOpenClawApiKeySecretKey(source)
            ?? throw new ArgumentException("OpenClaw API key source is required for OpenClaw ApiKey authentication.");
        var envName = source switch
        {
            OpenClawApiKeySource.OpenAI => "OPENAI_API_KEY",
            OpenClawApiKeySource.Cursor => "CURSOR_API_KEY",
            OpenClawApiKeySource.Anthropic => "ANTHROPIC_API_KEY",
            _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Unknown OpenClaw API key source.")
        };
        return new OpenClawApiKeyEnvBinding(envName, secretKey);
    }

    /// <summary>
    /// Whether the session's start has to look up a stored API key. Every ApiKey session needs
    /// one, whatever its mode; an Auto session only bills the API when it runs unattended without
    /// a subscription login. The mode used to gate both, on the assumption that an interactive
    /// session is always a subscription login — untrue once an autonomous run can be converted
    /// to interactive (docs/session-mode-conversion.md).
    /// </summary>
    public static bool ResolvesApiKey(SessionMode mode, AgentAuthMode authMode) =>
        authMode == AgentAuthMode.ApiKey
        || (mode is SessionMode.Autonomous or SessionMode.Scheduled && authMode == AgentAuthMode.Auto);

    public static string? MissingCredentialDiagnostic(SessionRecord record, PodBuildContext context)
    {
        if (record.Mode == SessionMode.Interactive) return null;

        var available = record.AuthMode switch
        {
            AgentAuthMode.Subscription => context.HasSelectedSubscriptionCredential,
            AgentAuthMode.ApiKey => context.HasSelectedApiKey,
            AgentAuthMode.Auto when record.Agent == AgentKind.Claude =>
                context.HasSelectedSubscriptionCredential || context.HasSelectedApiKey,
            _ => false
        };
        return available
            ? null
            : $"[agent] Cannot start {record.Agent} {record.Mode} session: {record.AuthMode} credential is not stored.";
    }

    public static V1PodSpec Build(SessionRecord record, CreateSessionRequest request, PodBuildContext context)
    {
        var images = context.RuntimeImages;
        var runtimeImage = record.Agent switch
        {
            AgentKind.Codex => images.CodexImage,
            AgentKind.Cursor => images.CursorImage,
            AgentKind.OpenClaw => images.OpenClawImage,
            _ => images.ClaudeImage
        };
        var repos = NormalizeRepos(request);
        var hasRepo = repos.Count > 0;
        var hasMcp = !string.IsNullOrWhiteSpace(request.McpConfigJson);
        var customImage = string.IsNullOrWhiteSpace(request.Image) ? null : request.Image;
        var asRoot = request.RunAsRoot;
        var uid = asRoot ? 0L : 1000L;
        var home = asRoot ? "/root" : "/home/agent";
        var policy = AgentConfiguration.ResolvePolicy(request.Policy, request.AllowedTools);

        var podSecurity = new V1PodSecurityContext
        {
            RunAsNonRoot = !asRoot, RunAsUser = uid, RunAsGroup = uid, FsGroup = uid,
            SeccompProfile = new V1SeccompProfile { Type = "RuntimeDefault" }
        };
        V1SecurityContext ContainerSecurity() => new()
        {
            AllowPrivilegeEscalation = false, ReadOnlyRootFilesystem = !asRoot,
            RunAsNonRoot = !asRoot, RunAsUser = uid,
            Capabilities = asRoot ? null : new V1Capabilities { Drop = new List<string> { "ALL" } }
        };

        var credentialItems = new List<V1KeyToPath>
        {
            ProjectCredential("ssh_key"),
            ProjectCredential("known_hosts"),
            // No gitlab_token/github_token here on purpose. A PAT now reaches the pod only as a
            // host-bound line in the gitcreds store, so the raw token never lands in a file the
            // session can read and offer to an arbitrary host.
            ProjectCredential("git_user_name"),
            ProjectCredential("git_user_email")
        };
        var volumes = new List<V1Volume>
        {
            new() { Name = "workspace", EmptyDir = new V1EmptyDirVolumeSource() },
            new() { Name = "home", EmptyDir = new V1EmptyDirVolumeSource() },
            new() { Name = "tmp", EmptyDir = new V1EmptyDirVolumeSource() },
            new() { Name = "creds", Secret = new V1SecretVolumeSource { SecretName = context.CredentialsSecretName, Optional = true, DefaultMode = 0x1A0, Items = credentialItems } }
        };
        var mounts = new List<V1VolumeMount>
        {
            new() { Name = "workspace", MountPath = "/workspace" },
            new() { Name = "home", MountPath = home },
            new() { Name = "tmp", MountPath = "/tmp" },
            new() { Name = "creds", MountPath = "/secrets/creds", ReadOnlyProperty = true }
        };
        var gitCloneMounts = new List<V1VolumeMount>(mounts);
        var env = new List<V1EnvVar>
        {
            new() { Name = "HOME", Value = home },
            new() { Name = "AGENTHUB_MODE", Value = request.Mode.ToString().ToLowerInvariant() },
            new() { Name = "AGENTHUB_UI_MODE", Value = record.UiMode },
            new() { Name = "AGENTHUB_AUTH_MODE", Value = record.AuthMode.ToString().ToLowerInvariant() },
            new() { Name = "AGENTHUB_SESSION_ID", Value = record.Id },
            new() { Name = "AGENTHUB_CLAUDE_SESSION_ID", Value = record.AgentSessionId },
            new() { Name = "AGENTHUB_PORT", Value = context.Runtime.AgentPort.ToString() },
            // The agent needs its own address to hand the browser a preview URL for a
            // server it started itself; the browser reaches it by IP, not by name.
            new()
            {
                Name = "AGENTHUB_POD_IP",
                ValueFrom = new V1EnvVarSource
                {
                    FieldRef = new V1ObjectFieldSelector { FieldPath = "status.podIP" }
                }
            },
            new() { Name = "AGENTHUB_HAS_REPO", Value = hasRepo ? "1" : "0" },
            new() { Name = "AGENTHUB_WORKDIR", Value = repos.Count == 1 ? "/workspace/repo" : "/workspace" },
            new() { Name = "AGENTHUB_HAS_MCP", Value = hasMcp ? "1" : "0" },
            new() { Name = "AGENTHUB_BROWSER_ENABLED", Value = context.Runtime.BrowserEnabled && request.Mode != SessionMode.Scheduled ? "1" : "0" },
            new() { Name = "AGENTHUB_SPAWN_MCP_ENABLED", Value = context.Runtime.SpawnMcpEnabled && request.Mode != SessionMode.Scheduled ? "1" : "0" },
            new() { Name = "AGENTHUB_NETWORK_MCP_ENABLED", Value = context.Runtime.NetworkMcpEnabled && request.Mode != SessionMode.Scheduled ? "1" : "0" },
            new() { Name = "AGENTHUB_RESUME", Value = string.IsNullOrEmpty(context.StateGetUrl) ? "0" : "1" },
            new() { Name = "AGENTHUB_PROMPT", Value = request.Prompt ?? "" },
            new() { Name = "AGENTHUB_SYSTEM_PROMPT", Value = request.SystemPrompt ?? "" },
            // Claude and Codex read the flag off the session record on every tool call, so it can
            // be toggled while they run. Cursor and OpenClaw have no per-call hook to ask through:
            // their permission settings are fixed when the process starts, which is what this is
            // for. A session that changes the flag has to be restarted for those two.
            new() { Name = "AGENTHUB_AUTO_APPROVE", Value = record.AutoApprove ? "1" : "0" },
            new() { Name = "AGENTHUB_ALLOWED_TOOLS", Value = System.Text.Json.JsonSerializer.Serialize(policy.AllowedTools) },
            new() { Name = "AGENTHUB_ALLOWED_MCP_TOOLS", Value = System.Text.Json.JsonSerializer.Serialize(policy.AllowedMcpTools) },
            new() { Name = "AGENTHUB_ALLOWED_COMMANDS", Value = System.Text.Json.JsonSerializer.Serialize(policy.AllowedCommands) },
            new() { Name = "AGENTHUB_CALLBACK_URL", Value = context.CallbackUrl },
            new() { Name = "AGENTHUB_CALLBACK_TOKEN", Value = record.CallbackToken },
            new() { Name = "AGENTHUB_S3_INSECURE", Value = context.S3Insecure ? "1" : "0" },
            new() { Name = "AGENTHUB_STATE_PUT_URL", Value = context.StatePutUrl },
            new() { Name = "AGENTHUB_STATE_GET_URL", Value = context.StateGetUrl },
            new() { Name = "AGENTHUB_SCROLLBACK_PUT_URL", Value = context.ScrollbackPutUrl },
            new() { Name = "AGENTHUB_TRANSCRIPT_PUT_URL", Value = context.TranscriptPutUrl }
        };

        void AddSubscriptionVolume(string name, string secretName)
        {
            // The secret holds every account of the user (docs/provider-accounts.md); the pod gets
            // exactly one, projected under the file name the entrypoint has always looked for, so
            // /secrets/<provider>/<file> is unchanged and the index and the other accounts' files
            // never enter the pod. `Optional` doubles as "a missing key is skipped": a user with
            // no login yet, or an account removed since, yields an empty mount rather than a pod
            // that fails to set up its volumes.
            var accountId = context.SubscriptionAccountId ?? ProviderAccountSecret.LegacyId;
            volumes.Add(new V1Volume
            {
                Name = name,
                Secret = new V1SecretVolumeSource
                {
                    SecretName = secretName, Optional = true, DefaultMode = 0x1A0,
                    Items = new List<V1KeyToPath>
                    {
                        new() { Key = ProviderAccountSecret.Key(accountId, record.Agent), Path = ProviderAccountSecret.FileName(record.Agent) }
                    }
                }
            });
            mounts.Add(new V1VolumeMount { Name = name, MountPath = $"/secrets/{name}", ReadOnlyProperty = true });
        }

        void AddApiKey(string envName, string secretKey)
        {
            env.Add(new V1EnvVar
            {
                Name = envName,
                ValueFrom = new V1EnvVarSource
                {
                    SecretKeyRef = new V1SecretKeySelector
                    {
                        Name = context.CredentialsSecretName, Key = secretKey, Optional = true
                    }
                }
            });
        }

        switch ((record.Agent, record.AuthMode))
        {
            case (AgentKind.Claude, AgentAuthMode.Subscription):
                AddSubscriptionVolume("claude", context.ClaudeCredentialSecretName);
                break;
            case (AgentKind.Claude, AgentAuthMode.ApiKey):
                AddApiKey("ANTHROPIC_API_KEY", "anthropic_api_key");
                break;
            case (AgentKind.Codex, AgentAuthMode.Subscription):
                AddSubscriptionVolume("codex", context.CodexCredentialSecretName);
                break;
            case (AgentKind.Codex, AgentAuthMode.ApiKey):
                AddApiKey("CODEX_API_KEY", "openai_api_key");
                break;
            case (AgentKind.Cursor, AgentAuthMode.Subscription):
                AddSubscriptionVolume("cursor", context.CursorCredentialSecretName);
                break;
            case (AgentKind.Cursor, AgentAuthMode.ApiKey):
                AddApiKey("CURSOR_API_KEY", "cursor_api_key");
                break;
            case (AgentKind.OpenClaw, AgentAuthMode.Subscription):
                AddSubscriptionVolume("openclaw", context.OpenClawCredentialSecretName);
                break;
            case (AgentKind.OpenClaw, AgentAuthMode.ApiKey):
            {
                var binding = OpenClawApiKeyBinding(record.OpenClawApiKeySource);
                AddApiKey(binding.EnvName, binding.SecretKey);
                break;
            }
            case (AgentKind.Claude, AgentAuthMode.Auto):
                AddSubscriptionVolume("claude", context.ClaudeCredentialSecretName);
                AddApiKey("ANTHROPIC_API_KEY", "anthropic_api_key");
                break;
            default:
                throw new ArgumentException("Unsupported agent/authentication combination.");
        }

        if (hasMcp)
        {
            volumes.Add(new V1Volume { Name = "mcp", Secret = new V1SecretVolumeSource { SecretName = $"mcp-{record.Id}", DefaultMode = 0x1A0 } });
            mounts.Add(new V1VolumeMount { Name = "mcp", MountPath = "/secrets/mcp", ReadOnlyProperty = true });
        }
        if (context.HasGitCredentials)
        {
            volumes.Add(new V1Volume { Name = "gitcreds", Secret = new V1SecretVolumeSource { SecretName = $"gitcreds-{record.Id}", DefaultMode = 0x1A0 } });
            mounts.Add(new V1VolumeMount { Name = "gitcreds", MountPath = "/secrets/gitcreds", ReadOnlyProperty = true });
            gitCloneMounts.Add(new V1VolumeMount { Name = "gitcreds", MountPath = "/secrets/gitcreds", ReadOnlyProperty = true });
        }
        if (customImage is not null)
        {
            volumes.Add(new V1Volume { Name = "runtime", EmptyDir = new V1EmptyDirVolumeSource() });
            mounts.Add(new V1VolumeMount { Name = "runtime", MountPath = "/opt/agenthub", ReadOnlyProperty = true });
        }

        if (record.Agent == AgentKind.Codex)
        {
            volumes.Add(new V1Volume { Name = "codex-system-config", EmptyDir = new V1EmptyDirVolumeSource() });
            mounts.Add(new V1VolumeMount { Name = "codex-system-config", MountPath = "/etc/codex", ReadOnlyProperty = true });
        }

        if (record.Agent == AgentKind.Claude && context.Runtime.TelemetryEnabled)
        {
            var otlpBase = string.IsNullOrWhiteSpace(context.Runtime.TelemetryOtlpEndpoint)
                ? context.CallbackUrl[..context.CallbackUrl.LastIndexOf("/internal/sessions/", StringComparison.Ordinal)] + "/internal/otel"
                : context.Runtime.TelemetryOtlpEndpoint.TrimEnd('/');
            env.Add(new() { Name = "CLAUDE_CODE_ENABLE_TELEMETRY", Value = "1" });
            env.Add(new() { Name = "OTEL_METRICS_EXPORTER", Value = "otlp" });
            env.Add(new() { Name = "OTEL_TRACES_EXPORTER", Value = "none" });
            env.Add(new() { Name = "OTEL_LOGS_EXPORTER", Value = "none" });
            env.Add(new() { Name = "OTEL_EXPORTER_OTLP_PROTOCOL", Value = "http/protobuf" });
            env.Add(new() { Name = "OTEL_EXPORTER_OTLP_ENDPOINT", Value = otlpBase });
            env.Add(new() { Name = "OTEL_EXPORTER_OTLP_METRICS_ENDPOINT", Value = $"{otlpBase}/v1/metrics" });
            env.Add(new() { Name = "OTEL_EXPORTER_OTLP_METRICS_TEMPORALITY_PREFERENCE", Value = "delta" });
            env.Add(new() { Name = "OTEL_METRIC_EXPORT_INTERVAL", Value = "30000" });
            env.Add(new() { Name = "OTEL_RESOURCE_ATTRIBUTES", Value = $"agenthub.session_id={record.Id},agenthub.owner={context.Owner}" });
        }

        var initContainers = new List<V1Container>();
        if (customImage is not null)
        {
            var copyScript = record.Agent switch
            {
                AgentKind.Codex => """
                    set -e
                    mkdir -p /opt/agenthub/bin /opt/agenthub/lib
                    cp -r /opt/session-agent /opt/agenthub/session-agent
                    cp /usr/local/bin/node /opt/agenthub/bin/node
                    cp -r /usr/local/lib/node_modules /opt/agenthub/lib/node_modules
                    cp /usr/local/bin/entrypoint.sh /opt/agenthub/entrypoint.sh
                    # codex launcher: resolve the symlink target of the global npm install and link it
                    target=$(readlink -f /usr/local/bin/codex)
                    ln -sf "/opt/agenthub/${target#/usr/local/}" /opt/agenthub/bin/codex
                    chmod -R a+rX /opt/agenthub
                    chmod +x /opt/agenthub/bin/node /opt/agenthub/entrypoint.sh "$(readlink -f /opt/agenthub/bin/codex)"
                    echo "Runtime copied to /opt/agenthub."
                    """,
                AgentKind.Cursor => """
                    set -e
                    mkdir -p /opt/agenthub/bin /opt/agenthub/lib /opt/agenthub/share
                    cp -r /opt/session-agent /opt/agenthub/session-agent
                    cp /usr/local/bin/node /opt/agenthub/bin/node
                    cp -r /usr/local/lib/node_modules /opt/agenthub/lib/node_modules
                    cp /usr/local/bin/entrypoint.sh /opt/agenthub/entrypoint.sh
                    # agent lives under /usr/local/share/cursor-agent; copy the tree so /opt/agenthub symlinks resolve
                    cp -a /usr/local/share/cursor-agent /opt/agenthub/share/cursor-agent
                    target=$(readlink -f /usr/local/bin/agent)
                    ln -sf "/opt/agenthub/${target#/usr/local/}" /opt/agenthub/bin/agent
                    if [ -e /usr/local/bin/cursor-agent ]; then
                      cursor_target=$(readlink -f /usr/local/bin/cursor-agent)
                      ln -sf "/opt/agenthub/${cursor_target#/usr/local/}" /opt/agenthub/bin/cursor-agent
                    fi
                    chmod -R a+rX /opt/agenthub
                    chmod +x /opt/agenthub/bin/node /opt/agenthub/entrypoint.sh "$(readlink -f /opt/agenthub/bin/agent)"
                    echo "Runtime copied to /opt/agenthub."
                    """,
                AgentKind.OpenClaw => """
                    set -e
                    mkdir -p /opt/agenthub/bin /opt/agenthub/lib
                    cp -r /opt/session-agent /opt/agenthub/session-agent
                    cp /usr/local/bin/node /opt/agenthub/bin/node
                    cp -r /usr/local/lib/node_modules /opt/agenthub/lib/node_modules
                    cp /usr/local/bin/entrypoint.sh /opt/agenthub/entrypoint.sh
                    # openclaw launcher: resolve the symlink target of the global npm install and link it
                    target=$(readlink -f /usr/local/bin/openclaw)
                    ln -sf "/opt/agenthub/${target#/usr/local/}" /opt/agenthub/bin/openclaw
                    chmod -R a+rX /opt/agenthub
                    chmod +x /opt/agenthub/bin/node /opt/agenthub/entrypoint.sh "$(readlink -f /opt/agenthub/bin/openclaw)"
                    echo "Runtime copied to /opt/agenthub."
                    """,
                _ => """
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
                    """
            };
            initContainers.Add(new V1Container
            {
                Name = "copy-runtime", Image = runtimeImage,
                ImagePullPolicy = context.RuntimeImages.PullPolicy,
                Command = new List<string> { "/bin/sh", "-c", copyScript },
                VolumeMounts = new List<V1VolumeMount> { new() { Name = "runtime", MountPath = "/opt/agenthub" } },
                SecurityContext = ContainerSecurity()
            });
        }

        if (record.Agent == AgentKind.Codex)
        {
            var managedRuntime = customImage is null ? "/opt/session-agent/codex" : "/opt/agenthub/session-agent/codex";
            var requirementsScript = $"""
                node /opt/session-agent/codex/project-requirements.js \
                  /etc/codex/requirements.toml /codex-system-config/requirements.toml {managedRuntime}
                """;
            initContainers.Add(new V1Container
            {
                Name = "prepare-codex-system-config",
                Image = runtimeImage,
                ImagePullPolicy = context.RuntimeImages.PullPolicy,
                Command = new List<string> { "/bin/sh", "-c", requirementsScript },
                VolumeMounts = new List<V1VolumeMount>
                {
                    new() { Name = "codex-system-config", MountPath = "/codex-system-config" }
                },
                SecurityContext = ContainerSecurity()
            });
        }

        if (hasRepo)
        {
            var reposEnv = string.Join("\n", DestFor(repos).Select(x => $"{x.dest}\t{x.repo.Branch}\t{x.repo.Url}"));
            const string cloneScript = """
                set -e
                if [ -f /secrets/creds/ssh_key ]; then
                  cp /secrets/creds/ssh_key /tmp/id && chmod 600 /tmp/id
                  export GIT_SSH_COMMAND="ssh -i /tmp/id -o IdentitiesOnly=yes -o UserKnownHostsFile=/secrets/creds/known_hosts -o StrictHostKeyChecking=yes"
                fi
                # HTTPS remotes: one credential store holds both connected-provider OAuth tokens
                # and manually stored PATs, each entry bound to its own host. There is deliberately
                # no second, host-less credential helper — the one that used to serve a stored
                # GitLab PAT answered for any host that returned 401. Copy the store to a writable
                # path: the secret mount is read-only, so git cannot take its lock there. $HOME is
                # shared with the agent container, so rebuild the helper list idempotently.
                git config --global --unset-all credential.helper 2>/dev/null || true
                if [ -f /secrets/gitcreds/credentials ]; then
                  cp /secrets/gitcreds/credentials "$HOME/.git-credentials" && chmod 600 "$HOME/.git-credentials"
                  git config --global credential.helper store
                fi
                TAB=$(printf '\t')
                printf '%s\n' "$REPOS" | while IFS="$TAB" read -r dest branch url; do
                  [ -z "$url" ] && continue
                  echo "Cloning $url (branch: ${branch:-default}) -> /workspace/$dest"
                  if [ -n "$branch" ]; then git clone --branch "$branch" "$url" "/workspace/$dest"
                  else git clone "$url" "/workspace/$dest"; fi
                done
                echo "Clone finished."
                """;
            initContainers.Add(new V1Container
            {
                Name = "git-clone", Image = context.Runtime.GitCloneImage,
                Command = new List<string> { "/bin/sh", "-c", cloneScript },
                Env = new List<V1EnvVar>
                {
                    new() { Name = "REPOS", Value = reposEnv },
                    new() { Name = "HOME", Value = home }
                },
                VolumeMounts = gitCloneMounts, SecurityContext = ContainerSecurity()
            });
        }

        var agent = new V1Container
        {
            Name = "agent", Image = customImage ?? runtimeImage,
            ImagePullPolicy = customImage is null ? context.RuntimeImages.PullPolicy : "IfNotPresent",
            Command = customImage is null ? null : new List<string> { "/bin/bash", "/opt/agenthub/entrypoint.sh" },
            Ports = new List<V1ContainerPort> { new() { ContainerPort = context.Runtime.AgentPort, Name = "term" } },
            Env = env, VolumeMounts = mounts, SecurityContext = ContainerSecurity(),
            Resources = new V1ResourceRequirements
            {
                Requests = new Dictionary<string, ResourceQuantity> { ["cpu"] = new(request.Cpu), ["memory"] = new(request.Memory) },
                Limits = new Dictionary<string, ResourceQuantity> { ["cpu"] = new(context.Runtime.MaxCpu), ["memory"] = new(context.Runtime.MaxMemory) }
            },
            ReadinessProbe = new V1Probe
            {
                TcpSocket = new V1TCPSocketAction { Port = context.Runtime.AgentPort },
                InitialDelaySeconds = 5, PeriodSeconds = 10
            }
        };

        return new V1PodSpec
        {
            RestartPolicy = "Never",
            AutomountServiceAccountToken = false,
            ServiceAccountName = "agenthub-agent",
            SecurityContext = podSecurity,
            EnableServiceLinks = false,
            ImagePullSecrets = string.IsNullOrEmpty(context.Runtime.ImagePullSecret)
                ? null
                : new List<V1LocalObjectReference> { new() { Name = context.Runtime.ImagePullSecret } },
            InitContainers = initContainers.Count > 0 ? initContainers : null,
            Containers = new List<V1Container> { agent },
            Volumes = volumes,
            RuntimeClassName = string.IsNullOrEmpty(context.Runtime.RuntimeClassName) ? null : context.Runtime.RuntimeClassName
        };
    }

    private static V1KeyToPath ProjectCredential(string key) =>
        new() { Key = key, Path = key };

    private static List<RepoRef> NormalizeRepos(CreateSessionRequest request)
    {
        if (request.Repos.Count > 0)
            return request.Repos.Where(r => !string.IsNullOrWhiteSpace(r.Url)).ToList();
        return string.IsNullOrWhiteSpace(request.RepoUrl)
            ? new List<RepoRef>()
            : new List<RepoRef> { new() { Url = request.RepoUrl!, Branch = request.RepoBranch } };
    }

    private static IEnumerable<(RepoRef repo, string dest)> DestFor(List<RepoRef> repos)
    {
        if (repos.Count == 1) { yield return (repos[0], "repo"); yield break; }
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var repo in repos)
        {
            var baseName = RepoDirName(repo.Url);
            var dest = baseName;
            var suffix = 1;
            while (!used.Add(dest)) dest = $"{baseName}-{suffix++}";
            yield return (repo, dest);
        }
    }

    private static string RepoDirName(string url)
    {
        var value = url.TrimEnd('/');
        var slash = value.LastIndexOf('/');
        var name = slash >= 0 ? value[(slash + 1)..] : value;
        if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        name = System.Text.RegularExpressions.Regex.Replace(name, "[^A-Za-z0-9._-]", "-");
        return string.IsNullOrEmpty(name) ? "repo" : name;
    }
}
