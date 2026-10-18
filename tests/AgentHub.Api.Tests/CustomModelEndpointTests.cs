using System.Text;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using k8s.Models;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>
/// CustomEndpoint authentication (docs/custom-model-endpoints.md): what a request may carry, what
/// the record keeps across updates and copies, and the environment the pod is given.
/// </summary>
public class CustomModelEndpointTests
{
    private const string Url = "https://llm.example.com/v1";
    private const string Model = "qwen3-coder-30b";

    [Theory]
    [InlineData(AgentKind.Claude)]
    [InlineData(AgentKind.Codex)]
    [InlineData(AgentKind.OpenCode)]
    public void Create_AcceptsTheVerifiedAgents_AndTrimsTheBase(AgentKind agent)
    {
        AgentConfiguration.ValidateForCreate(agent, AgentAuthMode.CustomEndpoint);

        var (url, model) = AgentConfiguration.ValidateModelEndpoint(
            agent, AgentAuthMode.CustomEndpoint, " https://llm.example.com/v1/ ", $" {Model} ");

        Assert.Equal(Url, url);
        Assert.Equal(Model, model);
    }

    [Theory]
    [InlineData(AgentKind.Cursor)]
    [InlineData(AgentKind.OpenClaw)]
    public void Create_RejectsAgentsThatCannotUseAnEndpoint_NamingTheAgent(AgentKind agent)
    {
        var error = Assert.Throws<ArgumentException>(() =>
            AgentConfiguration.ValidateModelEndpoint(agent, AgentAuthMode.CustomEndpoint, Url, Model));

        Assert.Contains(agent.ToString(), error.Message);
        Assert.Contains("custom model endpoint", error.Message);
    }

    [Theory]
    [InlineData(null, Model, "URL is required")]
    [InlineData(Url, null, "model name is required")]
    [InlineData("llm.example.com/v1", Model, "absolute")]
    [InlineData("ftp://llm.example.com/v1", Model, "absolute")]
    [InlineData("https://user:sk-secret@llm.example.com/v1", Model, "not in the URL")]
    [InlineData("https://llm.example.com/v1?key=sk-secret", Model, "without query")]
    [InlineData(Url, "two words", "without spaces")]
    public void Create_RejectsAnIncompleteOrUnsafeEndpoint(string? url, string? model, string expected)
    {
        var error = Assert.Throws<ArgumentException>(() =>
            AgentConfiguration.ValidateModelEndpoint(AgentKind.Claude, AgentAuthMode.CustomEndpoint, url, model));

        Assert.Contains(expected, error.Message);
    }

    [Theory]
    [InlineData(AgentAuthMode.Subscription)]
    [InlineData(AgentAuthMode.ApiKey)]
    public void EndpointFields_AreRefusedWithAnyOtherMode(AgentAuthMode authMode)
    {
        Assert.Throws<ArgumentException>(() =>
            AgentConfiguration.ValidateModelEndpoint(AgentKind.Claude, authMode, Url, null));
        Assert.Equal((null, null), AgentConfiguration.ValidateModelEndpoint(AgentKind.Claude, authMode, null, " "));
    }

    [Fact]
    public void CustomEndpoint_IsNotAProviderAccountSession()
    {
        Assert.False(AgentConfiguration.UsesProviderAccount(AgentAuthMode.CustomEndpoint));
        Assert.False(AgentConfiguration.UsesProviderAccount(AgentAuthMode.ApiKey));
        Assert.True(AgentConfiguration.UsesProviderAccount(AgentAuthMode.Subscription));
        Assert.True(AgentConfiguration.UsesProviderAccount(AgentAuthMode.Auto));
    }

    [Fact]
    public void Update_SwitchingToCustomEndpoint_NeedsAnEndpointInTheSameRequest()
    {
        var record = Record(AgentKind.Codex, AgentAuthMode.Subscription);

        Assert.Throws<ArgumentException>(() => SessionUpdateValidator.Validate(record,
            new UpdateSessionRequest { AuthMode = AgentAuthMode.CustomEndpoint }));
        SessionUpdateValidator.Validate(record, new UpdateSessionRequest
        {
            AuthMode = AgentAuthMode.CustomEndpoint, ModelEndpointUrl = Url, ModelName = Model
        });
    }

    [Fact]
    public void Update_LeavingCustomEndpoint_DropsTheStoredEndpointInsteadOfFailingOnIt()
    {
        var record = Record(AgentKind.Claude, AgentAuthMode.CustomEndpoint, Url, Model);
        var request = new UpdateSessionRequest { AuthMode = AgentAuthMode.Subscription };

        SessionUpdateValidator.Validate(record, request);

        Assert.Equal((null, null), SessionUpdateValidator.ModelEndpointAfterUpdate(record, request));
    }

    [Fact]
    public void Update_ChangingOnlyTheModel_KeepsTheStoredUrl()
    {
        var record = Record(AgentKind.OpenCode, AgentAuthMode.CustomEndpoint, Url, Model);

        var after = SessionUpdateValidator.ModelEndpointAfterUpdate(record, new UpdateSessionRequest { ModelName = "other-model" });

        Assert.Equal((Url, "other-model"), after);
    }

    [Fact]
    public void Update_MovingAnEndpointSessionToCursor_IsRefused()
    {
        var record = Record(AgentKind.Claude, AgentAuthMode.CustomEndpoint, Url, Model);

        Assert.Throws<ArgumentException>(() => SessionUpdateValidator.Validate(record,
            new UpdateSessionRequest { Agent = AgentKind.Cursor }));
    }

    [Fact]
    public void Update_OfAScheduledSessionsEndpoint_IsARuntimeChangeAndRefused()
    {
        var record = Record(AgentKind.Claude, AgentAuthMode.CustomEndpoint, Url, Model);
        record.Mode = SessionMode.Scheduled;

        Assert.Throws<ArgumentException>(() => SessionUpdateValidator.Validate(record,
            new UpdateSessionRequest { ModelName = "other-model" }));
    }

    [Fact]
    public void Duplicate_KeepsTheEndpointWhileTheModeStays_AndDropsItOtherwise()
    {
        var source = Record(AgentKind.Claude, AgentAuthMode.CustomEndpoint, Url, Model);

        var same = SessionDuplication.CopyableRequest(source, new DuplicateSessionRequest("copy", null, false));
        var switched = SessionDuplication.CopyableRequest(source,
            new DuplicateSessionRequest("copy", null, false, AuthMode: AgentAuthMode.Subscription));

        Assert.Equal((Url, Model), (same.ModelEndpointUrl, same.ModelName));
        Assert.Null(same.CredentialId);
        Assert.Equal((null, null), (switched.ModelEndpointUrl, switched.ModelName));
    }

    [Theory]
    [InlineData(AgentKind.Claude, "runtime-claude")]
    [InlineData(AgentKind.Codex, "runtime-codex")]
    [InlineData(AgentKind.OpenCode, "runtime-opencode")]
    public void Pod_GetsTheProviderNeutralEndpointEnv_AndNoProviderCredential(AgentKind agent, string image)
    {
        var record = Record(agent, AgentAuthMode.CustomEndpoint, Url, Model);
        var request = new CreateSessionRequest { Agent = agent, AuthMode = AgentAuthMode.CustomEndpoint, Mode = SessionMode.Interactive };

        var pod = AgentPodSpecFactory.Build(record, request, Context());

        var container = Assert.Single(pod.Containers);
        Assert.Equal(image, container.Image);
        Assert.Equal("customendpoint", Env(container, "AGENTHUB_AUTH_MODE").Value);
        Assert.Equal(Url, Env(container, "AGENTHUB_MODEL_ENDPOINT_URL").Value);
        Assert.Equal(Model, Env(container, "AGENTHUB_MODEL_NAME").Value);
        var key = Env(container, "AGENTHUB_MODEL_API_KEY").ValueFrom.SecretKeyRef;
        Assert.Equal("creds-owner", key.Name);
        Assert.Equal("custom_endpoint_api_key", key.Key);
        // A keyless server is a normal setup, so a missing key must not keep the pod from starting.
        Assert.True(key.Optional);
        foreach (var provider in new[] { "ANTHROPIC_API_KEY", "CODEX_API_KEY", "OPENCODE_API_KEY", "OPENAI_API_KEY" })
            Assert.DoesNotContain(container.Env, e => e.Name == provider);
        foreach (var login in new[] { "claude", "codex", "opencode" })
            Assert.DoesNotContain(pod.Volumes, v => v.Name == login);
    }

    [Theory]
    [InlineData(SessionMode.Autonomous)]
    [InlineData(SessionMode.Scheduled)]
    public void UnattendedStart_IsNotBlockedByAMissingEndpointKey(SessionMode mode)
    {
        var record = Record(AgentKind.Codex, AgentAuthMode.CustomEndpoint, Url, Model);
        record.Mode = mode;

        Assert.Null(AgentPodSpecFactory.MissingCredentialDiagnostic(record, Context()));
    }

    [Fact]
    public void EndpointKey_IsStoredReportedAndClearedLikeEveryOtherKey()
    {
        var stored = CredentialSecretFactory.CreateGeneralSecret("creds-owner", "sessions", "owner", null,
            new UserCredentials { CustomEndpointApiKey = "sk-synthetic-not-real" });

        Assert.Equal("sk-synthetic-not-real", Encoding.UTF8.GetString(stored.Data["custom_endpoint_api_key"]));
        Assert.True(CredentialSecretFactory.CredentialStatus(stored.Data).CustomEndpointApiKey);
        Assert.Equal("custom_endpoint_api_key", CredentialSecretFactory.CredentialKey("customEndpointApiKey"));

        var cleared = CredentialSecretFactory.CreateGeneralSecret("creds-owner", "sessions", "owner", stored.Data,
            new UserCredentials { Clear = ["customEndpointApiKey"] });

        Assert.False(cleared.Data.ContainsKey("custom_endpoint_api_key"));
        Assert.False(CredentialSecretFactory.CredentialStatus(cleared.Data).CustomEndpointApiKey);
    }

    [PostgreSqlFact]
    public async Task EndpointFields_SurviveARoundTrip_AndTheColumnsBeforeThemStillMap()
    {
        await using var database = await PostgresSharingDatabase.CreateAsync();
        var record = Record(AgentKind.Claude, AgentAuthMode.CustomEndpoint, Url, Model);
        record.SecretIdsJson = "[\"s1\"]";

        await database.UpsertSessionAsync(record);
        var stored = (await database.GetSessionAsync("owner", record.Id))!;

        Assert.Equal(AgentAuthMode.CustomEndpoint, stored.AuthMode);
        Assert.Equal(Url, stored.ModelEndpointUrl);
        Assert.Equal(Model, stored.ModelName);
        // The store maps by ordinal; the column before the new ones must still be itself.
        Assert.Equal("[\"s1\"]", stored.SecretIdsJson);

        stored.AuthMode = AgentAuthMode.Subscription;
        stored.ModelEndpointUrl = null;
        stored.ModelName = null;
        await database.UpsertSessionAsync(stored);
        var switched = (await database.GetSessionAsync("owner", record.Id))!;

        Assert.Null(switched.ModelEndpointUrl);
        Assert.Null(switched.ModelName);
    }

    private static V1EnvVar Env(V1Container container, string name) => Assert.Single(container.Env, e => e.Name == name);

    private static SessionRecord Record(AgentKind agent, AgentAuthMode auth, string? url = null, string? model = null) => new()
    {
        Id = "session-id",
        Owner = "owner",
        Title = "Session",
        Mode = SessionMode.Interactive,
        Agent = agent,
        AuthMode = auth,
        ModelEndpointUrl = url,
        ModelName = model,
        AgentSessionId = "agent-session-id",
        CallbackToken = "callback-token"
    };

    private static PodBuildContext Context() => new()
    {
        Owner = "owner",
        CredentialsSecretName = "creds-owner",
        ClaudeCredentialSecretName = "claude-owner",
        CodexCredentialSecretName = "codex-owner",
        CursorCredentialSecretName = "cursor-owner",
        OpenClawCredentialSecretName = "openclaw-owner",
        OpenCodeCredentialSecretName = "opencode-owner",
        CallbackUrl = "http://callback/internal/sessions/session-id",
        StatePutUrl = "http://s3/state-put",
        StateGetUrl = "",
        ScrollbackPutUrl = "http://s3/scroll-put",
        TranscriptPutUrl = "http://s3/transcript-put",
        RuntimeImages = new AgentRuntimeImages(
            "runtime-claude", "runtime-codex", "runtime-cursor", "runtime-openclaw", "runtime-opencode", "Always")
    };
}
