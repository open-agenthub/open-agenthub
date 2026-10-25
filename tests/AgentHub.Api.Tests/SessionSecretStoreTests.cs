using System.Text;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>
/// The generic secret store (docs/session-secrets.md): what may be stored under which name, what a
/// listing gives back, and which values a session's own copies receive.
/// </summary>
public class SessionSecretStoreTests
{
    private static readonly DateTime Now = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);

    private static UpsertSessionSecretRequest Env(string name, string? value = "s3cr3t-value") => new(name, "env", value, null);
    private static UpsertSessionSecretRequest File(string name, string? value = "s3cr3t-value") => new(name, "file", value, null);

    [Fact]
    public void Upsert_KeepsTheValueOutOfTheIndex_AndTheListingCarriesNoValue()
    {
        var data = new Dictionary<string, byte[]>();
        var entry = SessionSecretStore.Upsert(data, new("DEPLOY_TOKEN", "env", "tok-123456789", "CI deploy"), Now);

        Assert.DoesNotContain("tok-123456789", Encoding.UTF8.GetString(data[SessionSecretStore.IndexKey]));
        Assert.Equal("tok-123456789", Encoding.UTF8.GetString(SessionSecretStore.Value(data, entry.Id)!));
        var info = SessionSecretStore.Info(Assert.Single(SessionSecretStore.Read(data)));
        Assert.Equal(new SessionSecretInfo(entry.Id, "DEPLOY_TOKEN", "env", "CI deploy", Now), info);
        // The record has no value property at all: nothing a client could be handed by mistake.
        Assert.DoesNotContain(typeof(SessionSecretInfo).GetProperties(), p => p.Name.Contains("Value"));
    }

    [Fact]
    public void Upsert_ById_UpdatesInPlace_KeepingIdAndValueWhenNoneIsSent_AndCanRename()
    {
        var data = new Dictionary<string, byte[]>();
        var first = SessionSecretStore.Upsert(data, Env("API_KEY", "first-value-1"), Now);

        var edited = SessionSecretStore.Upsert(data, new("api_key", "file", null, "now a file", first.Id), Now.AddHours(1));

        Assert.Equal(first.Id, edited.Id);
        Assert.Equal(("api_key", "file"), (edited.Name, edited.Delivery));
        Assert.Equal("first-value-1", Encoding.UTF8.GetString(SessionSecretStore.Value(data, first.Id)!));
        var rotated = SessionSecretStore.Upsert(data, File("renamed.json", "second-value-2") with { Id = first.Id }, Now.AddHours(2));
        Assert.Equal(first.Id, rotated.Id);
        Assert.Equal("renamed.json", rotated.Name);
        Assert.Equal("second-value-2", Encoding.UTF8.GetString(SessionSecretStore.Value(data, first.Id)!));
        Assert.Single(SessionSecretStore.Read(data));
    }

    [Fact]
    public void Upsert_WithoutId_StoresASecondSecretUnderTheSameName()
    {
        var data = new Dictionary<string, byte[]>();
        var prod = SessionSecretStore.Upsert(data, new("API_KEY", "env", "prod-value-1", "production"), Now);
        var staging = SessionSecretStore.Upsert(data, new("API_KEY", "env", "staging-value-2", "staging"), Now);

        Assert.NotEqual(prod.Id, staging.Id);
        Assert.Equal(2, SessionSecretStore.Read(data).Count(e => e.Name == "API_KEY"));
        Assert.Equal("prod-value-1", Encoding.UTF8.GetString(SessionSecretStore.Value(data, prod.Id)!));
        Assert.Equal("staging-value-2", Encoding.UTF8.GetString(SessionSecretStore.Value(data, staging.Id)!));
    }

    [Fact]
    public void Upsert_WithAnUnknownId_IsRefused_RatherThanStoredAsNew()
    {
        var data = new Dictionary<string, byte[]>();
        Assert.Throws<ArgumentException>(() => SessionSecretStore.Upsert(data, Env("API_KEY") with { Id = "nope" }, Now));
        Assert.Empty(SessionSecretStore.Read(data));
    }

    [Theory]
    [InlineData("HOME")]
    [InlineData("PATH")]
    [InlineData("path")]
    [InlineData("TMPDIR")]
    [InlineData("AGENTHUB_CALLBACK_TOKEN")]
    [InlineData("AGENTHUB_ANYTHING")]
    [InlineData("LD_PRELOAD")]
    [InlineData("NODE_OPTIONS")]
    [InlineData("BASH_ENV")]
    [InlineData("GIT_SSH_COMMAND")]
    [InlineData("ANTHROPIC_API_KEY")]
    [InlineData("ANTHROPIC_BASE_URL")]
    [InlineData("OPENAI_API_KEY")]
    [InlineData("CODEX_HOME")]
    [InlineData("OTEL_EXPORTER_OTLP_ENDPOINT")]
    public void ReservedVariables_AreRefused(string name)
    {
        var e = Assert.Throws<ArgumentException>(() => SessionSecretStore.Upsert(new Dictionary<string, byte[]>(), Env(name), Now));
        Assert.Contains("cannot be overridden", e.Message);
    }

    [Fact]
    public void EveryVariableThePodSpecSets_IsReserved()
    {
        // The list is checked against the spec itself, so a variable added to the pod later
        // cannot be overridden by a secret just because nobody remembered this list.
        var spec = SessionSecretPodSpecTests.Build(withSecrets: false);
        foreach (var env in spec.Containers.Single().Env)
            Assert.True(SessionSecretStore.IsReservedEnvName(env.Name), $"{env.Name} is set by the runtime but not reserved");
    }

    [Theory]
    [InlineData("env", "1ABC")]
    [InlineData("env", "MY-VAR")]
    [InlineData("env", "A B")]
    [InlineData("file", ".env")]
    [InlineData("file", "..data")]
    [InlineData("file", "../escape")]
    [InlineData("file", "dir/key.pem")]
    [InlineData("file", "-rf")]
    [InlineData("file", "")]
    public void InvalidNames_AreRefused(string delivery, string name) =>
        Assert.Throws<ArgumentException>(() =>
            SessionSecretStore.Upsert(new Dictionary<string, byte[]>(), new(name, delivery, "value-1234", null), Now));

    [Theory]
    [InlineData("env", "GITLAB_TOKEN")]
    [InlineData("env", "_private")]
    [InlineData("file", "service-account.json")]
    [InlineData("file", "id_ed25519")]
    public void OrdinaryNames_AreAccepted(string delivery, string name) =>
        SessionSecretStore.Upsert(new Dictionary<string, byte[]>(), new(name, delivery, "value-1234", null), Now);

    [Fact]
    public void Limits_AreEnforcedAtTheEdge()
    {
        var data = new Dictionary<string, byte[]>();
        Assert.Throws<ArgumentException>(() => SessionSecretStore.Upsert(data, Env("BIG", new string('x', SessionSecretStore.MaxValueBytes + 1)), Now));
        Assert.Throws<ArgumentException>(() => SessionSecretStore.Upsert(data, Env("EMPTY", ""), Now));
        Assert.Throws<ArgumentException>(() => SessionSecretStore.Upsert(data, Env("NUL", "a\0b"), Now));
        Assert.Throws<ArgumentException>(() => SessionSecretStore.Upsert(data, Env("NEW", null), Now));
        Assert.Throws<ArgumentException>(() => SessionSecretStore.Upsert(data, new("X", "volume", "v", null), Now));

        for (var i = 0; i < SessionSecretStore.MaxEntries; i++)
            SessionSecretStore.Upsert(data, Env($"S{i}", new string('x', SessionSecretStore.MaxValueBytes)), Now);
        Assert.Throws<ArgumentException>(() => SessionSecretStore.Upsert(data, Env("ONE_MORE"), Now));
        // A full store stays well inside the 1 MiB a Kubernetes secret may hold.
        Assert.True(data.Values.Sum(v => v.Length) < 600 * 1024);
    }

    [Fact]
    public void Remove_DropsIndexEntryAndValue_AndIsIdempotent()
    {
        var data = new Dictionary<string, byte[]>();
        var a = SessionSecretStore.Upsert(data, Env("A_TOKEN"), Now);
        SessionSecretStore.Upsert(data, Env("B_TOKEN"), Now);

        Assert.True(SessionSecretStore.Remove(data, a.Id));
        Assert.False(SessionSecretStore.Remove(data, a.Id));
        Assert.Null(SessionSecretStore.Value(data, a.Id));
        Assert.Equal("B_TOKEN", Assert.Single(SessionSecretStore.Read(data)).Name);
    }

    [Fact]
    public void CorruptIndex_ReadsAsEmpty()
    {
        var data = new Dictionary<string, byte[]> { [SessionSecretStore.IndexKey] = "{not json"u8.ToArray() };
        Assert.Empty(SessionSecretStore.Read(data));
    }
}

/// <summary>Which secrets a session receives: projects select, sessions narrow (docs/session-secrets.md).</summary>
public class SessionSecretSelectionTests
{
    private static readonly IReadOnlyList<SessionSecretStore.Entry> Stored =
    [
        new("a", "A_TOKEN", "env", null, DateTime.UtcNow),
        new("b", "b.pem", "file", null, DateTime.UtcNow),
        new("c", "C_TOKEN", "env", null, DateTime.UtcNow)
    ];

    [Fact]
    public void OutsideAProject_NullIsNothing()
        => Assert.Empty(SessionSecretSelection.Effective(null, null, [], Stored));

    [Fact]
    public void InAProject_NullFollowsTheProject()
        => Assert.Equal(["a", "b"], SessionSecretSelection.Effective(null, "p", ["a", "b"], Stored));

    [Fact]
    public void AnExplicitList_IsCutToTheProjectsCurrentSelection()
        // The project was narrowed after the session was created: the secret goes, it is not kept.
        => Assert.Equal(["a"], SessionSecretSelection.Effective(["a", "c"], "p", ["a", "b"], Stored));

    [Fact]
    public void OutsideAProject_AnExplicitListIsUsedAsIs()
        => Assert.Equal(["c"], SessionSecretSelection.Effective(["c"], null, [], Stored));

    [Fact]
    public void RemovedSecrets_AreSkipped()
        => Assert.Equal(["a"], SessionSecretSelection.Effective(["a", "gone"], null, [], Stored));

    [Fact]
    public void Normalize_RefusesUnknownIds_AndReadsTheWildcardAsFollowTheProject()
    {
        Assert.Throws<ArgumentException>(() => SessionSecretSelection.Normalize(["nope"], Stored));
        Assert.Null(SessionSecretSelection.Normalize(["*"], Stored));
        Assert.Equal(["a"], SessionSecretSelection.Normalize([" a ", "a", ""], Stored));
        Assert.Empty(SessionSecretSelection.Normalize([], Stored)!);
    }

    [Fact]
    public void EnsureWithinProject_RefusesWhatTheProjectDoesNotSelect()
    {
        Assert.Throws<ArgumentException>(() => SessionSecretSelection.EnsureWithinProject(["c"], "p", ["a"]));
        SessionSecretSelection.EnsureWithinProject(["a"], "p", ["a"]);
        SessionSecretSelection.EnsureWithinProject(["c"], null, []);
        SessionSecretSelection.EnsureWithinProject(null, "p", []);
    }

    [Fact]
    public void Parse_ReadsAnUnreadableColumnAsNothing()
    {
        // Unlike the git token selection: here the safe reading of a corrupt column is "none".
        Assert.Empty(SessionSecretSelection.Parse("[not json")!);
        Assert.Null(SessionSecretSelection.Parse(null));
        Assert.Empty(SessionSecretSelection.ParseProject(null));
    }

    [Fact]
    public void Duplicate_CopiesTheSelectionOnlyWithinTheSameProject()
    {
        var source = new SessionRecord { Id = "s", Owner = "o", Title = "t", CallbackToken = "t", ProjectId = "p", SecretIdsJson = "[\"a\"]" };
        Assert.Equal(["a"], SessionDuplication.CopyableRequest(source, new("copy", "p", false)).SecretIds);
        Assert.Null(SessionDuplication.CopyableRequest(source, new("copy", "other", false)).SecretIds);
        Assert.Equal(["b"], SessionDuplication.CopyableRequest(source, new("copy", "other", false, SecretIds: ["b"])).SecretIds);
    }
}

/// <summary>How the selection reaches the pod: two per-session copies, one mounted, one referenced.</summary>
public class SessionSecretPodSpecTests
{
    internal static k8s.Models.V1PodSpec Build(bool withSecrets, IReadOnlyList<string>? envNames = null,
        AgentKind agent = AgentKind.Claude, AgentAuthMode auth = AgentAuthMode.ApiKey, bool repo = true)
    {
        var request = new CreateSessionRequest
        {
            Agent = agent, AuthMode = auth, Mode = SessionMode.Interactive,
            Repos = repo ? [new RepoRef { Url = "https://git.example.com/a/b.git" }] : []
        };
        var record = new SessionRecord
        {
            Id = "sid", Owner = "owner", Title = "Session", Mode = SessionMode.Interactive,
            Agent = agent, AuthMode = auth, AgentSessionId = "agent-session-id", CallbackToken = "callback-token",
            OpenClawApiKeySource = agent == AgentKind.OpenClaw && auth == AgentAuthMode.ApiKey ? OpenClawApiKeySource.OpenAI : null
        };
        var context = new PodBuildContext
        {
            Owner = "owner", CredentialsSecretName = "creds-owner",
            ClaudeCredentialSecretName = "claude-owner", CodexCredentialSecretName = "codex-owner",
            CursorCredentialSecretName = "cursor-owner", OpenClawCredentialSecretName = "openclaw-owner",
            OpenCodeCredentialSecretName = "opencode-owner", HasSelectedApiKey = true,
            CallbackUrl = "http://callback/internal/sessions/sid",
            StatePutUrl = "", StateGetUrl = "", ScrollbackPutUrl = "", TranscriptPutUrl = "",
            RuntimeImages = new AgentRuntimeImages("claude", "codex", "cursor", "openclaw", "opencode", "Always"),
            Runtime = new AgentPodRuntimeSettings { TelemetryEnabled = true },
            HasSessionSecrets = withSecrets,
            SecretEnvNames = envNames ?? Array.Empty<string>()
        };
        return AgentPodSpecFactory.Build(record, request, context);
    }

    [Fact]
    public void TheFileCopy_IsMountedWholeUnderHome_SoChangesReachARunningPod()
    {
        var spec = Build(withSecrets: true);
        var volume = Assert.Single(spec.Volumes, v => v.Name == "session-secrets");
        Assert.Equal("sessionsecrets-sid", volume.Secret.SecretName);
        // items or subPath would freeze the set of files at pod creation.
        Assert.Null(volume.Secret.Items);
        var mount = Assert.Single(spec.Containers.Single().VolumeMounts, m => m.Name == "session-secrets");
        Assert.Equal("/home/agent/.secrets", mount.MountPath);
        Assert.True(mount.ReadOnlyProperty);
        Assert.Null(mount.SubPath);
        Assert.Equal("/home/agent/.secrets", spec.Containers.Single().Env.Single(e => e.Name == "AGENTHUB_SECRETS_DIR").Value);
    }

    [Fact]
    public void Variables_AreReferencedFromTheEnvCopy_AndOnlyInTheAgentContainer()
    {
        var spec = Build(withSecrets: true, envNames: ["DEPLOY_TOKEN", "SENTRY_DSN"]);
        var env = spec.Containers.Single().Env;
        foreach (var name in new[] { "DEPLOY_TOKEN", "SENTRY_DSN" })
        {
            var entry = Assert.Single(env, e => e.Name == name);
            Assert.Null(entry.Value);
            Assert.Equal("sessionenv-sid", entry.ValueFrom.SecretKeyRef.Name);
            Assert.Equal(name, entry.ValueFrom.SecretKeyRef.Key);
            Assert.True(entry.ValueFrom.SecretKeyRef.Optional);
        }
        Assert.Equal("DEPLOY_TOKEN,SENTRY_DSN", env.Single(e => e.Name == "AGENTHUB_SECRET_ENV").Value);
        // The clone runs before the agent and needs none of it.
        var clone = Assert.Single(spec.InitContainers, c => c.Name == "git-clone");
        Assert.DoesNotContain(clone.VolumeMounts, m => m.Name == "session-secrets");
        Assert.DoesNotContain(clone.Env, e => e.Name == "DEPLOY_TOKEN");
    }

    [Fact]
    public void RootSessions_GetTheDirectoryUnderTheirOwnHome()
    {
        var request = new CreateSessionRequest { Agent = AgentKind.Claude, AuthMode = AgentAuthMode.ApiKey, RunAsRoot = true };
        var record = new SessionRecord { Id = "sid", Owner = "o", Title = "t", CallbackToken = "t", Agent = AgentKind.Claude, AuthMode = AgentAuthMode.ApiKey };
        var spec = AgentPodSpecFactory.Build(record, request, new PodBuildContext
        {
            Owner = "o", CredentialsSecretName = "c", ClaudeCredentialSecretName = "c", CodexCredentialSecretName = "c",
            CursorCredentialSecretName = "c", OpenClawCredentialSecretName = "c", OpenCodeCredentialSecretName = "c",
            CallbackUrl = "http://callback/internal/sessions/sid", StatePutUrl = "", StateGetUrl = "",
            ScrollbackPutUrl = "", TranscriptPutUrl = "",
            RuntimeImages = new AgentRuntimeImages("a", "b", "c", "d", "e", "Always"), HasSessionSecrets = true
        });
        Assert.Equal("/root/.secrets", spec.Containers.Single().VolumeMounts.Single(m => m.Name == "session-secrets").MountPath);
    }

    [Theory]
    [InlineData(AgentKind.Claude, AgentAuthMode.Subscription)]
    [InlineData(AgentKind.Claude, AgentAuthMode.Auto)]
    [InlineData(AgentKind.Codex, AgentAuthMode.ApiKey)]
    [InlineData(AgentKind.Cursor, AgentAuthMode.ApiKey)]
    [InlineData(AgentKind.OpenClaw, AgentAuthMode.ApiKey)]
    [InlineData(AgentKind.OpenCode, AgentAuthMode.ApiKey)]
    public void EveryVariableTheRuntimeSets_IsReserved_ForEveryAgent(AgentKind agent, AgentAuthMode auth)
    {
        foreach (var env in Build(withSecrets: true, agent: agent, auth: auth).Containers.Single().Env)
            Assert.True(SessionSecretStore.IsReservedEnvName(env.Name), $"{env.Name} is set by the runtime but not reserved");
    }

    [Fact]
    public void Delivery_SplitsTheSelectionByDelivery_AndLeavesOutWhatIsNotSelected()
    {
        var store = new Dictionary<string, byte[]>();
        var now = DateTime.UtcNow;
        var env = SessionSecretStore.Upsert(store, new("DEPLOY_TOKEN", "env", "env-value-123", null), now);
        var file = SessionSecretStore.Upsert(store, new("sa.json", "file", "{\"k\":1}", null), now);
        SessionSecretStore.Upsert(store, new("OTHER", "env", "not-selected-1", null), now);

        var delivery = SessionSecretDeliveryFactory.Build("sid", "ns",
            new Dictionary<string, string> { ["agenthub.dev/owner"] = "u-1", ["agenthub.dev/session"] = "sid" },
            store, [env.Id, file.Id]);

        Assert.Equal(["sa.json"], delivery.Files.Data.Keys);
        Assert.Equal(["DEPLOY_TOKEN"], delivery.Env.Data.Keys);
        Assert.Equal(["DEPLOY_TOKEN"], delivery.EnvNames);
        Assert.Equal("env-value-123", Encoding.UTF8.GetString(delivery.Env.Data["DEPLOY_TOKEN"]));
        // Labelled with owner and session: the account purge and the session delete find them.
        Assert.Equal("u-1", delivery.Files.Metadata.Labels["agenthub.dev/owner"]);
        Assert.Equal("sid", delivery.Env.Metadata.Labels["agenthub.dev/session"]);
        Assert.Equal(SessionSecretDeliveryFactory.FilesComponent, delivery.Files.Metadata.Labels["agenthub.dev/component"]);
    }
}

/// <summary>The backend copy of the masking rules, used on hook messages the chat relays post.</summary>
public class SecretMaskTests
{
    private static KeyValuePair<string, byte[]> S(string name, string value) => new(name, Encoding.UTF8.GetBytes(value));

    [Fact]
    public void MasksRawJsonEscapedAndMultiLineForms()
    {
        var pem = "-----BEGIN KEY-----\nMIIEvQIBADANBgkqhkiG9w0B\n-----END KEY-----\n";
        var secrets = new[] { S("API_TOKEN", "tok-0123456789"), S("key.pem", pem), S("Q", "va\"lue-with-quote") };

        Assert.Equal("use [secret:API_TOKEN] now", SecretMask.Apply("use tok-0123456789 now", secrets));
        Assert.Equal("{\"t\":\"[secret:Q]\"}", SecretMask.Apply("{\"t\":\"va\\\"lue-with-quote\"}", secrets));
        Assert.DoesNotContain("MIIEvQIBADANBgkqhkiG9w0B", SecretMask.Apply("line MIIEvQIBADANBgkqhkiG9w0B\r\n", secrets));
        Assert.DoesNotContain("MIIEvQ", SecretMask.Apply(pem.Replace("\n", "\r\n"), secrets));
    }

    [Fact]
    public void ShortValues_AreLeftAlone()
        => Assert.Equal("port 1234 is open", SecretMask.Apply("port 1234 is open", [S("PORT", "1234")]));

    [Fact]
    public void ALongerValueContainingAShorterOne_IsMaskedWhole()
        => Assert.Equal("[secret:LONG]", SecretMask.Apply("abcdefgh-ijklmnop", [S("SHORT", "abcdefgh"), S("LONG", "abcdefgh-ijklmnop")]));
}
