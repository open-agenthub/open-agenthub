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
    public void StoringAPat_RejectsAValueThatWouldCorruptTheStore()
    {
        var bad = new UserCredentials { GitlabToken = "glpat\nhttps://oauth2:x@evil.example" };
        var badHost = new UserCredentials { GithubToken = "ghp_ok", GithubHost = "https://github.com" };

        Assert.Throws<ArgumentException>(() =>
            CredentialSecretFactory.CreateGeneralSecret("creds", "ns", "u", null, bad));
        Assert.Throws<ArgumentException>(() =>
            CredentialSecretFactory.CreateGeneralSecret("creds", "ns", "u", null, badHost));
    }

    [Fact]
    public void StoringAPat_KeepsTokenAndHostTogetherAndReportsThemAsStored()
    {
        var secret = CredentialSecretFactory.CreateGeneralSecret("creds", "ns", "u", null,
            new UserCredentials
            {
                GithubToken = "ghp_secret", GithubHost = " github.example.com ",
                GitlabToken = "glpat-secret", GitlabHost = "gitlab.example.com"
            });

        Assert.Equal("ghp_secret", Encoding.UTF8.GetString(secret.Data["github_token"]));
        // Trimmed, so a pasted host with stray whitespace does not fail host validation later.
        Assert.Equal("github.example.com", Encoding.UTF8.GetString(secret.Data["github_host"]));

        var status = CredentialSecretFactory.CredentialStatus(secret.Data);
        Assert.True(status.GithubToken);
        Assert.True(status.GithubHost);
        Assert.True(status.GitlabToken);
        Assert.True(status.GitlabHost);
    }

    [Fact]
    public void StoringAPatWithoutItsHost_IsRefusedRatherThanDefaultedToThePublicInstance()
    {
        // Defaulting would be silently wrong twice for a self-hosted instance: the clone gets no
        // credential for the host it uses, and glab/gh are configured for a host nobody named.
        // The mechanism this replaced worked against any host, so a default would have broken a
        // working self-hosted setup.
        var error = Assert.Throws<ArgumentException>(() =>
            CredentialSecretFactory.CreateGeneralSecret("creds", "ns", "u", null,
                new UserCredentials { GitlabToken = "glpat-secret" }));
        Assert.Contains("host it belongs to", error.Message);

        Assert.Throws<ArgumentException>(() =>
            CredentialSecretFactory.CreateGeneralSecret("creds", "ns", "u", null,
                new UserCredentials { GithubToken = "ghp_secret" }));
    }

    [Fact]
    public void RotatingAToken_DoesNotRequireRestatingAHostThatIsAlreadyStored()
    {
        var existing = Secret(
            ("github_token", "ghp_old"), ("github_host", "github.example.com"));

        var secret = CredentialSecretFactory.CreateGeneralSecret("creds", "ns", "u", existing,
            new UserCredentials { GithubToken = "ghp_new" });

        Assert.Equal("ghp_new", Encoding.UTF8.GetString(secret.Data["github_token"]));
        Assert.Equal("github.example.com", Encoding.UTF8.GetString(secret.Data["github_host"]));
    }

    [Fact]
    public void ClearingOnlyTheHost_CannotLeaveATokenBoundToNothing()
    {
        var existing = Secret(
            ("github_token", "ghp_secret"), ("github_host", "github.example.com"));

        Assert.Throws<ArgumentException>(() =>
            CredentialSecretFactory.CreateGeneralSecret("creds", "ns", "u", existing,
                new UserCredentials { Clear = ["githubHost"] }));
    }

    [Fact]
    public void AnUnrelatedCredentialUpdate_IsNotBlockedByATokenStoredBeforeHostsExisted()
    {
        // Pre-existing rows have no host. Refusing every later save would make such an account
        // unable to store anything until it noticed a field it was not editing.
        var legacy = Secret(("gitlab_token", "glpat-old"));

        var secret = CredentialSecretFactory.CreateGeneralSecret("creds", "ns", "u", legacy,
            new UserCredentials { AnthropicApiKey = "sk-ant-x" });

        Assert.Equal("sk-ant-x", Encoding.UTF8.GetString(secret.Data["anthropic_api_key"]));
        Assert.Equal("glpat-old", Encoding.UTF8.GetString(secret.Data["gitlab_token"]));
    }

    [Fact]
    public void ClearingAPat_RemovesTokenAndHostIndependently()
    {
        var existing = Secret(
            ("github_token", "ghp_secret"), ("github_host", "github.example.com"));

        var secret = CredentialSecretFactory.CreateGeneralSecret("creds", "ns", "u", existing,
            new UserCredentials { Clear = ["githubToken", "githubHost"] });

        Assert.False(secret.Data.ContainsKey("github_token"));
        Assert.False(secret.Data.ContainsKey("github_host"));
    }
}
