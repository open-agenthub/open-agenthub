using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using k8s.Models;
using Xunit;

namespace AgentHub.Api.Tests;

public class AgentPodSpecFactoryTests
{
    [Theory]
    [InlineData(AgentKind.Claude, AgentAuthMode.Subscription, "subscription", "runtime-claude", "claude", null)]
    [InlineData(AgentKind.Claude, AgentAuthMode.ApiKey, "apikey", "runtime-claude", null, "ANTHROPIC_API_KEY")]
    [InlineData(AgentKind.Codex, AgentAuthMode.Subscription, "subscription", "runtime-codex", "codex", null)]
    [InlineData(AgentKind.Codex, AgentAuthMode.ApiKey, "apikey", "runtime-codex", null, "CODEX_API_KEY")]
    public void Build_CredentialSelection_MountsOnlySelectedCredential(
        AgentKind agent, AgentAuthMode auth, string expectedAuthMode, string expectedImage, string? expectedVolume, string? expectedEnv)
    {
        var pod = Build(agent, auth);
        var container = Assert.Single(pod.Containers);
        var projectedCredentialItems = Assert.Single(pod.Volumes, v => v.Name == "creds").Secret.Items;
        Assert.NotNull(projectedCredentialItems);
        var projectedCredentialKeys = projectedCredentialItems.Select(i => i.Key).ToList();

        Assert.Equal(expectedImage, container.Image);
        Assert.Contains(container.Env, e => e.Name == "AGENTHUB_AUTH_MODE" &&
            e.Value == expectedAuthMode && e.ValueFrom is null);
        Assert.Equal(expectedVolume is not null, pod.Volumes.Any(v => v.Name == expectedVolume));
        Assert.Equal(expectedEnv is not null, container.Env.Any(e => e.Name == expectedEnv));
        Assert.Equal(expectedVolume == "claude", pod.Volumes.Any(v => v.Name == "claude"));
        Assert.Equal(expectedVolume == "codex", pod.Volumes.Any(v => v.Name == "codex"));
        Assert.Equal(expectedEnv == "ANTHROPIC_API_KEY", container.Env.Any(e => e.Name == "ANTHROPIC_API_KEY"));
        Assert.Equal(expectedEnv == "CODEX_API_KEY", container.Env.Any(e => e.Name == "CODEX_API_KEY"));
        Assert.Contains("ssh_key", projectedCredentialKeys);
        Assert.Contains("known_hosts", projectedCredentialKeys);
        Assert.Contains("gitlab_token", projectedCredentialKeys);
        Assert.Contains("git_user_name", projectedCredentialKeys);
        Assert.Contains("git_user_email", projectedCredentialKeys);
        Assert.DoesNotContain("anthropic_api_key", projectedCredentialKeys);
        Assert.DoesNotContain("openai_api_key", projectedCredentialKeys);
        if (expectedEnv is not null)
        {
            var apiKey = Assert.Single(container.Env, e => e.Name == expectedEnv);
            Assert.Equal("creds-owner", apiKey.ValueFrom?.SecretKeyRef?.Name);
            Assert.Equal(expectedEnv == "CODEX_API_KEY" ? "openai_api_key" : "anthropic_api_key",
                apiKey.ValueFrom?.SecretKeyRef?.Key);
        }
    }

    [Theory]
    [InlineData(AgentKind.Claude, AgentAuthMode.Subscription)]
    [InlineData(AgentKind.Claude, AgentAuthMode.ApiKey)]
    [InlineData(AgentKind.Codex, AgentAuthMode.Subscription)]
    [InlineData(AgentKind.Codex, AgentAuthMode.ApiKey)]
    public void Build_GitCloneMountsOnlyWorkspaceHomeTmpAndGitCredentials(AgentKind agent, AgentAuthMode auth)
    {
        var pod = Build(agent, auth, request => request with { RepoUrl = "https://example.test/repo.git" });
        var clone = Assert.Single(pod.InitContainers, c => c.Name == "git-clone");

        Assert.Equal(new[] { "workspace", "home", "tmp", "creds" }, clone.VolumeMounts.Select(m => m.Name));
        Assert.DoesNotContain(clone.VolumeMounts, m => m.Name is "claude" or "codex" or "mcp" or "runtime");
        Assert.DoesNotContain(clone.Env, e => e.ValueFrom?.SecretKeyRef is not null);
    }


    [Theory]
    [InlineData(AgentKind.Claude, AgentAuthMode.Subscription, "runtime-claude", "claude")]
    [InlineData(AgentKind.Claude, AgentAuthMode.ApiKey, "runtime-claude", "claude")]
    [InlineData(AgentKind.Codex, AgentAuthMode.Subscription, "runtime-codex", "codex")]
    [InlineData(AgentKind.Codex, AgentAuthMode.ApiKey, "runtime-codex", "codex")]
    public void Build_CustomImageCopyInitUsesSelectedRuntimeImage(
        AgentKind agent, AgentAuthMode auth, string expectedRuntimeImage, string expectedLauncher)
    {
        var pod = Build(agent, auth, request => request with { Image = "custom/runtime:1" });
        var copyRuntime = Assert.Single(pod.InitContainers, c => c.Name == "copy-runtime");

        Assert.Equal("custom/runtime:1", Assert.Single(pod.Containers).Image);
        Assert.Equal(expectedRuntimeImage, copyRuntime.Image);
        Assert.Contains($"/usr/local/bin/{expectedLauncher}", Assert.Single(copyRuntime.Command, c => c.Contains("target=")));
    }

    [Theory]
    [InlineData(AgentKind.Claude, null, false)]
    [InlineData(AgentKind.Claude, "custom/runtime:1", true)]
    [InlineData(AgentKind.Codex, null, false)]
    [InlineData(AgentKind.Codex, "custom/runtime:1", true)]
    public void Build_RuntimeMountIsReadOnlyForCustomAgentAndWritableOnlyForCopyInit(
        AgentKind agentKind, string? customImage, bool expectsInjectedRuntime)
    {
        var pod = Build(agentKind, AgentAuthMode.Subscription, request => request with
        {
            Image = customImage
        });
        var agent = Assert.Single(pod.Containers);

        if (!expectsInjectedRuntime)
        {
            Assert.DoesNotContain(agent.VolumeMounts, mount => mount.Name == "runtime");
            Assert.DoesNotContain(pod.InitContainers ?? [], container => container.Name == "copy-runtime");
            return;
        }

        var agentRuntime = Assert.Single(agent.VolumeMounts, mount => mount.Name == "runtime");
        var copyRuntime = Assert.Single(pod.InitContainers, container => container.Name == "copy-runtime");
        var initRuntime = Assert.Single(copyRuntime.VolumeMounts, mount => mount.Name == "runtime");
        Assert.True(agentRuntime.ReadOnlyProperty);
        Assert.NotEqual(true, initRuntime.ReadOnlyProperty);
    }

    [Theory]
    [InlineData(null, "/opt/session-agent/codex")]
    [InlineData("custom/runtime:1", "/opt/agenthub/session-agent/codex")]
    public void Build_CodexAutonomousProjectsRuntimeOwnedSystemRequirementsReadOnly(
        string? customImage, string expectedManagedRuntime)
    {
        var pod = Build(AgentKind.Codex, AgentAuthMode.ApiKey, request => request with
        {
            Mode = SessionMode.Autonomous,
            Prompt = "work",
            Image = customImage
        });
        var agent = Assert.Single(pod.Containers);
        var requirements = Assert.Single(pod.InitContainers, c => c.Name == "prepare-codex-system-config");
        var command = Assert.Single(requirements.Command, value => value.Contains("requirements.toml"));

        Assert.NotNull(Assert.Single(pod.Volumes, v => v.Name == "codex-system-config").EmptyDir);
        var agentMount = Assert.Single(agent.VolumeMounts, m => m.Name == "codex-system-config");
        Assert.Equal("/etc/codex", agentMount.MountPath);
        Assert.True(agentMount.ReadOnlyProperty);
        Assert.Equal("runtime-codex", requirements.Image);
        Assert.Contains("/etc/codex/requirements.toml", command);
        Assert.Contains("/opt/session-agent/codex/project-requirements.js", command);
        Assert.Contains(expectedManagedRuntime, command);
    }


    [Fact]
    public void Build_ClaudeAutomationPassesEachStructuredPolicyCategoryToTheDriver()
    {
        var pod = Build(AgentKind.Claude, AgentAuthMode.Subscription, request => request with
        {
            Mode = SessionMode.Autonomous,
            Prompt = "work",
            Policy = new AgentPolicy
            {
                AllowedTools = ["Read", "Edit"],
                AllowedMcpTools = ["mcp__docs__search", "mcp__git__*"],
                AllowedCommands = ["git status", "npm test"]
            }
        });
        var env = Assert.Single(pod.Containers).Env.ToDictionary(item => item.Name);

        Assert.Equal("[\"Read\",\"Edit\"]", env["AGENTHUB_ALLOWED_TOOLS"].Value);
        Assert.Equal("[\"mcp__docs__search\",\"mcp__git__*\"]",
            env["AGENTHUB_ALLOWED_MCP_TOOLS"].Value);
        Assert.Equal("[\"git status\",\"npm test\"]",
            env["AGENTHUB_ALLOWED_COMMANDS"].Value);
    }


    [Fact]
    public void Build_ClaudeAutoPreservesLegacyPodShape()
    {
        var pod = Build(AgentKind.Claude, AgentAuthMode.Auto);
        var container = Assert.Single(pod.Containers);

        Assert.Equal("runtime-claude", container.Image);
        Assert.Null(container.Command);
        Assert.Contains(container.Env, e => e.Name == "AGENTHUB_AUTH_MODE" &&
            e.Value == "auto" && e.ValueFrom is null);
        Assert.Equal(new[] { "workspace", "home", "tmp", "creds", "claude" }, pod.Volumes.Select(v => v.Name));
        Assert.Equal(new[] { "workspace", "home", "tmp", "creds", "claude" }, container.VolumeMounts.Select(v => v.Name));
        Assert.Contains(container.Env, e => e.Name == "ANTHROPIC_API_KEY" &&
            e.ValueFrom?.SecretKeyRef?.Name == "creds-owner" &&
            e.ValueFrom.SecretKeyRef.Key == "anthropic_api_key" && e.ValueFrom.SecretKeyRef.Optional == true);
        var projectedCredentialItems = Assert.Single(pod.Volumes, v => v.Name == "creds").Secret.Items;
        Assert.NotNull(projectedCredentialItems);
        var projectedCredentialKeys = projectedCredentialItems.Select(i => i.Key).ToList();
        Assert.DoesNotContain("anthropic_api_key", projectedCredentialKeys);
        Assert.DoesNotContain("openai_api_key", projectedCredentialKeys);
        Assert.DoesNotContain(container.Env, e => e.Name == "CODEX_API_KEY");
        Assert.Empty(pod.InitContainers ?? Array.Empty<V1Container>());
        Assert.False(pod.AutomountServiceAccountToken);
        Assert.False(pod.EnableServiceLinks);
        Assert.Equal("agenthub-agent", pod.ServiceAccountName);
        Assert.Equal(1000, pod.SecurityContext.RunAsUser);
        Assert.True(pod.SecurityContext.RunAsNonRoot);
        Assert.False(container.SecurityContext.AllowPrivilegeEscalation);
        Assert.True(container.SecurityContext.ReadOnlyRootFilesystem);
        Assert.Equal(new[] { "ALL" }, container.SecurityContext.Capabilities.Drop);
        Assert.Equal("500m", container.Resources.Requests["cpu"].ToString());
        Assert.Equal("1Gi", container.Resources.Requests["memory"].ToString());
        Assert.Equal("2", container.Resources.Limits["cpu"].ToString());
        Assert.Equal("4Gi", container.Resources.Limits["memory"].ToString());
        Assert.Equal("7681", container.ReadinessProbe.TcpSocket.Port.Value);
        Assert.Equal(5, container.ReadinessProbe.InitialDelaySeconds);
        Assert.Equal(10, container.ReadinessProbe.PeriodSeconds);
    }

    [Fact]
    public void Build_CronJobTemplateReusesExactPodSpec()
    {
        var pod = Build(AgentKind.Codex, AgentAuthMode.Subscription);
        var template = new V1PodTemplateSpec { Spec = pod };

        Assert.Same(pod, template.Spec);
    }

    [Theory]
    [InlineData(SessionMode.Interactive, AgentKind.Codex, AgentAuthMode.Subscription, false, false, null)]
    [InlineData(SessionMode.Autonomous, AgentKind.Codex, AgentAuthMode.Subscription, false, false,
        "[agent] Cannot start Codex Autonomous session: Subscription credential is not stored.")]
    [InlineData(SessionMode.Scheduled, AgentKind.Claude, AgentAuthMode.ApiKey, false, false,
        "[agent] Cannot start Claude Scheduled session: ApiKey credential is not stored.")]
    [InlineData(SessionMode.Autonomous, AgentKind.Codex, AgentAuthMode.ApiKey, true, false, null)]
    [InlineData(SessionMode.Autonomous, AgentKind.Claude, AgentAuthMode.Subscription, false, true, null)]
    public void CredentialSelection_MissingCredentialDiagnostic(
        SessionMode mode, AgentKind agent, AgentAuthMode auth, bool hasApiKey, bool hasSubscription, string? expected)
    {
        var record = Record(agent, auth, mode);
        var context = Context() with { HasSelectedApiKey = hasApiKey, HasSelectedSubscriptionCredential = hasSubscription };

        Assert.Equal(expected, AgentPodSpecFactory.MissingCredentialDiagnostic(record, context));
    }

    private static V1PodSpec Build(AgentKind agent, AgentAuthMode auth,
        Func<CreateSessionRequest, CreateSessionRequest>? customize = null)
    {
        var request = new CreateSessionRequest
        {
            Agent = agent,
            AuthMode = auth,
            Mode = SessionMode.Interactive
        };
        if (customize is not null) request = customize(request);
        return AgentPodSpecFactory.Build(Record(agent, auth, request.Mode), request, Context());
    }

    private static SessionRecord Record(AgentKind agent, AgentAuthMode auth, SessionMode mode) => new()
    {
        Id = "session-id",
        Owner = "owner",
        Title = "Session",
        Mode = mode,
        Agent = agent,
        AuthMode = auth,
        AgentSessionId = "agent-session-id",
        CallbackToken = "callback-token"
    };

    private static PodBuildContext Context() => new()
    {
        Owner = "owner",
        CredentialsSecretName = "creds-owner",
        ClaudeCredentialSecretName = "claude-owner",
        CodexCredentialSecretName = "codex-owner",
        CallbackUrl = "http://callback/internal/sessions/session-id",
        StatePutUrl = "http://s3/state-put",
        StateGetUrl = "",
        ScrollbackPutUrl = "http://s3/scroll-put",
        RuntimeImages = new AgentRuntimeImages("runtime-claude", "runtime-codex", "Always"),
        Runtime = new AgentPodRuntimeSettings
        {
            AgentPort = 7681,
            GitCloneImage = "git-clone",
            MaxCpu = "2",
            MaxMemory = "4Gi"
        }
    };
}
