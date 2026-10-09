using System.Text;
using AgentHub.Api.Models;
using AgentHub.Api.Services;
using Xunit;

namespace AgentHub.Api.Tests;

public class ManualGitCredentialsTests
{
    private static Dictionary<string, byte[]> Secret(params (string key, string value)[] entries) =>
        entries.ToDictionary(e => e.key, e => Encoding.UTF8.GetBytes(e.value));

    [Fact]
    public void StoredPat_BecomesAHostBoundStoreEntry_NotAGlobalHelper()
    {
        var lines = ManualGitCredentials.Lines(Secret(
            ("gitlab_token", "glpat-secret"), ("gitlab_host", "gitlab.example.com")));

        // One entry, bound to the host the user named. The helper this replaced was registered
        // globally and answered with the token for any host that returned 401.
        Assert.Equal(["https://oauth2:glpat-secret@gitlab.example.com"], lines);
    }

    [Fact]
    public void AStoredTokenWithoutAHost_FallsBackToThePublicInstance()
    {
        Assert.Equal(
            ["https://oauth2:glpat-secret@gitlab.com"],
            ManualGitCredentials.Lines(Secret(("gitlab_token", "glpat-secret"))));
        Assert.Equal(
            ["https://x-access-token:ghp_secret@github.com"],
            ManualGitCredentials.Lines(Secret(("github_token", "ghp_secret"))));
    }

    [Fact]
    public void TheUserPartIsWhatTellsSetupCliAuthWhichCliToConfigure()
    {
        var lines = ManualGitCredentials.Lines(Secret(
            ("github_token", "ghp_secret"), ("gitlab_token", "glpat-secret")));

        // setup-cli-auth.sh keys on exactly these values to decide gh vs glab, so a manual PAT has
        // to look like an OAuth token of the same kind.
        Assert.Contains("https://x-access-token:ghp_secret@github.com", lines);
        Assert.Contains("https://oauth2:glpat-secret@gitlab.com", lines);
    }

    [Fact]
    public void ATokenContainingANewline_CannotAppendASecondEntry()
    {
        // The store is line-based: an accepted newline would add an entry for a host of the
        // attacker's choosing to the user's own credential file.
        Assert.False(ManualGitCredentials.IsValidToken("glpat-secret\nhttps://oauth2:x@evil.example"));
        Assert.Empty(ManualGitCredentials.Lines(Secret(
            ("gitlab_token", "glpat-secret\nhttps://oauth2:x@evil.example"))));

        Assert.False(ManualGitCredentials.IsValidToken(""));
        Assert.False(ManualGitCredentials.IsValidToken("has space"));
        Assert.False(ManualGitCredentials.IsValidToken("tab\there"));
        Assert.False(ManualGitCredentials.IsValidToken(new string('x', ManualGitCredentials.MaxTokenLength + 1)));
        Assert.True(ManualGitCredentials.IsValidToken("glpat-Abc_123-xyz"));
    }

    [Fact]
    public void AHostMustBeAHostname_NotAUrlOrAPath()
    {
        Assert.True(ManualGitCredentials.IsValidHost("github.com"));
        Assert.True(ManualGitCredentials.IsValidHost("git.example.com:8443"));
        Assert.True(ManualGitCredentials.IsValidHost("git-01.example.com"));

        // Each of these would either write a malformed entry or widen it beyond one host.
        Assert.False(ManualGitCredentials.IsValidHost("https://github.com"));
        Assert.False(ManualGitCredentials.IsValidHost("github.com/org"));
        Assert.False(ManualGitCredentials.IsValidHost("evil@github.com"));
        Assert.False(ManualGitCredentials.IsValidHost("github.com evil.example"));
        Assert.False(ManualGitCredentials.IsValidHost(""));
        Assert.False(ManualGitCredentials.IsValidHost(null));
    }

    [Fact]
    public void AnInvalidStoredValue_IsSkippedRatherThanBreakingTheSession()
    {
        // Values are validated when stored, so anything invalid here predates that validation.
        // Refusing to build a store would make such an account unable to start a session at all.
        var lines = ManualGitCredentials.Lines(Secret(
            ("gitlab_token", "glpat-good"), ("gitlab_host", "https://nope.example"),
            ("github_token", "ghp_good")));

        Assert.Equal(["https://x-access-token:ghp_good@github.com"], lines);
    }

    [Fact]
    public void ConnectedProviderTokensWin_OverAManualPatForTheSameHost()
    {
        // git's store helper answers with the first matching entry, so OAuth has to come first:
        // it is the token that gets refreshed, and it is what the user had before PATs joined.
        var store = ManualGitCredentials.ComposeStore(
            "https://x-access-token:oauth-token@github.com\n",
            ManualGitCredentials.Lines(Secret(("github_token", "ghp_manual"))));

        Assert.Equal(
            "https://x-access-token:oauth-token@github.com\nhttps://x-access-token:ghp_manual@github.com\n",
            store);
    }

    [Fact]
    public void AStoreIsBuiltFromAPatAlone_WhichIsWhatMakesTheRouteWork()
    {
        // Before this, a user with only a manual PAT got no gitcreds secret at all and depended on
        // the global helper in the pod.
        Assert.Equal("https://oauth2:glpat-secret@gitlab.com\n",
            ManualGitCredentials.ComposeStore(null, ManualGitCredentials.Lines(
                Secret(("gitlab_token", "glpat-secret")))));

        Assert.Null(ManualGitCredentials.ComposeStore(null, Array.Empty<string>()));
        Assert.Null(ManualGitCredentials.ComposeStore("   ", Array.Empty<string>()));
    }

    [Fact]
    public void ComposeDoesNotRepeatAnIdenticalEntry()
    {
        var store = ManualGitCredentials.ComposeStore(
            "https://oauth2:same@gitlab.com\n",
            ["https://oauth2:same@gitlab.com"]);

        Assert.Equal("https://oauth2:same@gitlab.com\n", store);
    }

    [Fact]
    public void SeveralHostsOfOneKind_EachGetTheirOwnLine()
    {
        // The fixed per-kind slots could not hold this at all: a company GitLab and a personal
        // one meant choosing. The list emits one host-bound line per entry, in stored order.
        var data = Secret();
        GitPatStore.Upsert(data, new UpsertGitPatRequest { Kind = "gitlab", Host = "gitlab.example.com", Token = "glpat-work" });
        GitPatStore.Upsert(data, new UpsertGitPatRequest { Kind = "gitlab", Host = "gitlab.com", Token = "glpat-personal" });
        GitPatStore.Upsert(data, new UpsertGitPatRequest { Kind = "github", Host = "github.your-org.example", Token = "ghp_ent" });

        Assert.Equal(
        [
            "https://oauth2:glpat-work@gitlab.example.com",
            "https://oauth2:glpat-personal@gitlab.com",
            "https://x-access-token:ghp_ent@github.your-org.example"
        ], ManualGitCredentials.Lines(data));
    }

    [Fact]
    public void LegacySlotsAndListEntries_AreBothEmitted()
    {
        // A secret written before the list existed, plus one entry added since: the session must
        // see both until a write folds the legacy slots in.
        var data = Secret(("gitlab_token", "glpat-old"), ("gitlab_host", "gitlab.example.com"));
        GitPatStore.Upsert(data, new UpsertGitPatRequest { Kind = "github", Host = "github.com", Token = "ghp_new" });

        Assert.Equal(
        [
            "https://oauth2:glpat-old@gitlab.example.com",
            "https://x-access-token:ghp_new@github.com"
        ], ManualGitCredentials.Lines(data));
    }
}
