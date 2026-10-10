using System.Text;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>
/// A session names which stored git PATs it is built with (docs/credential-scopes.md). Null keeps
/// the old "every PAT" behaviour; the selection narrows the store lines, never widens them.
/// </summary>
public class GitPatSelectionTests
{
    private static Dictionary<string, byte[]> TwoHosts()
    {
        var data = new Dictionary<string, byte[]>();
        GitPatStore.Upsert(data, new UpsertGitPatRequest { Kind = "gitlab", Host = "gitlab.example.com", Token = "glpat-work" });
        GitPatStore.Upsert(data, new UpsertGitPatRequest { Kind = "github", Host = "github.com", Token = "ghp_personal" });
        return data;
    }

    [Fact]
    public void Null_MeansEveryStoredPat_AsBefore()
    {
        var stored = GitPatStore.Read(TwoHosts());

        Assert.Null(GitPatSelection.Normalize(null, stored));
        Assert.Equal(stored, GitPatSelection.Apply(stored, null));
        Assert.Equal(2, ManualGitCredentials.Lines(GitPatSelection.Apply(stored, null)).Count);
    }

    [Fact]
    public void AnEmptyList_MeansNoPatAtAll()
    {
        var stored = GitPatStore.Read(TwoHosts());

        var selected = GitPatSelection.Normalize([], stored);

        Assert.NotNull(selected);
        Assert.Empty(selected);
        Assert.Empty(GitPatSelection.Apply(stored, selected));
        // Connected providers still reach the store; the manual lines are simply absent.
        Assert.Equal("https://oauth2:oauth@gitlab.example.com\n",
            ManualGitCredentials.ComposeStore("https://oauth2:oauth@gitlab.example.com\n",
                ManualGitCredentials.Lines(GitPatSelection.Apply(stored, selected))));
    }

    [Fact]
    public void AnExplicitList_KeepsOnlyThoseHostsInStoredOrder()
    {
        var stored = GitPatStore.Read(TwoHosts());
        var github = stored.Single(e => e.Host == "github.com").Id;

        var selected = GitPatSelection.Normalize([" " + github + " ", github], stored);

        Assert.Equal([github], selected);
        Assert.Equal(["https://x-access-token:ghp_personal@github.com"],
            ManualGitCredentials.Lines(GitPatSelection.Apply(stored, selected)));
    }

    [Fact]
    public void TheWildcard_IsASpellingOfAll_SoAnUpdateCanGoBackToIt()
    {
        var stored = GitPatStore.Read(TwoHosts());
        Assert.Null(GitPatSelection.Normalize(["*"], stored));
        Assert.Null(GitPatSelection.Normalize([stored[0].Id, "*"], stored));
    }

    [Fact]
    public void AnUnknownId_IsRefusedWhileTheCallerIsStillThere()
    {
        var stored = GitPatStore.Read(TwoHosts());
        var error = Assert.Throws<ArgumentException>(() => GitPatSelection.Normalize(["nope"], stored));
        Assert.Contains("nope", error.Message);
    }

    [Fact]
    public void ARemovedPat_IsSkippedAtSpawnRatherThanFailingTheSession()
    {
        var data = TwoHosts();
        var stored = GitPatStore.Read(data);
        var removed = stored[0].Id;
        var kept = stored[1].Id;
        GitPatStore.Remove(data, removed);

        var lines = ManualGitCredentials.Lines(GitPatSelection.Apply(GitPatStore.Read(data), [removed, kept]));

        Assert.Equal(["https://x-access-token:ghp_personal@github.com"], lines);
    }

    [Fact]
    public void TheStoredForm_RoundTripsAndTreatsGarbageAsAll()
    {
        Assert.Null(GitPatSelection.Serialize(null));
        Assert.Equal("[]", GitPatSelection.Serialize([]));
        Assert.Equal(["a", "b"], GitPatSelection.Parse(GitPatSelection.Serialize(["a", "b"])));
        Assert.Null(GitPatSelection.Parse(null));
        Assert.Null(GitPatSelection.Parse("{not json"));
        Assert.Empty(GitPatSelection.Parse("[]")!);
    }

    [Fact]
    public void Duplicate_CopiesTheSelectionUnlessTheRequestReplacesIt()
    {
        var source = new SessionRecord
        {
            Id = "s", Owner = "alice", CallbackToken = "t", GitPatIdsJson = "[\"a\"]"
        };

        var copied = SessionDuplication.CopyableRequest(source, new DuplicateSessionRequest("copy", null, false));
        var replaced = SessionDuplication.CopyableRequest(source,
            new DuplicateSessionRequest("copy", null, false, GitPatIds: ["b"]));
        var cleared = SessionDuplication.CopyableRequest(source,
            new DuplicateSessionRequest("copy", null, false, GitPatIds: []));

        Assert.Equal(["a"], copied.GitPatIds);
        Assert.Equal(["b"], replaced.GitPatIds);
        Assert.Empty(cleared.GitPatIds!);
        var unrestricted = new SessionRecord { Id = "s", Owner = "alice", CallbackToken = "t", GitPatIdsJson = null };
        Assert.Null(SessionDuplication.CopyableRequest(unrestricted, new DuplicateSessionRequest("copy", null, false)).GitPatIds);
    }

    /// <summary>The gitcreds secret is created once for a CronJob, so changing the selection of a
    /// scheduled session would change the record while every run kept the old store.</summary>
    [Fact]
    public void UpdateValidator_TreatsTheSelectionAsARuntimeField()
    {
        var scheduled = new SessionRecord { Id = "s", Owner = "alice", CallbackToken = "t", Mode = SessionMode.Scheduled };
        var interactive = new SessionRecord { Id = "s", Owner = "alice", CallbackToken = "t", Mode = SessionMode.Interactive };

        Assert.Throws<ArgumentException>(() => SessionUpdateValidator.Validate(scheduled, new UpdateSessionRequest { GitPatIds = [] }));
        SessionUpdateValidator.Validate(interactive, new UpdateSessionRequest { GitPatIds = [] });
    }

    private static Dictionary<string, byte[]> Secret(params (string key, string value)[] entries) =>
        entries.ToDictionary(e => e.key, e => Encoding.UTF8.GetBytes(e.value));

    [Fact]
    public void LegacySlots_AreSelectableByTheirStableIds()
    {
        // A token that migrated from the old per-provider slot keeps a fixed id, so a session can
        // name it before any write has turned the slot into a list entry.
        var stored = GitPatStore.Read(Secret(("gitlab_token", "glpat-old"), ("gitlab_host", "gitlab.example.com")));
        var selected = GitPatSelection.Normalize([GitPatStore.LegacyGitLabId], stored);
        Assert.Equal(["https://oauth2:glpat-old@gitlab.example.com"],
            ManualGitCredentials.Lines(GitPatSelection.Apply(stored, selected)));
    }
}
