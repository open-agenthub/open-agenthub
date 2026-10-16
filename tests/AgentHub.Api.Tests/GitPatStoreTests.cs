using System.Text;
using System.Text.Json;
using AgentHub.Api.Models;
using AgentHub.Api.Services;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>
/// Git PATs are a list keyed by host rather than one fixed slot per provider kind. The fixed
/// slots could not hold a second GitLab host at all, and the legacy ones still in users' secrets
/// have to keep working until a write replaces them.
/// </summary>
public class GitPatStoreTests
{
    private static Dictionary<string, byte[]> Secret(params (string key, string value)[] entries) =>
        entries.ToDictionary(e => e.key, e => Encoding.UTF8.GetBytes(e.value));

    private static UpsertGitPatRequest Request(string kind, string host, string token) =>
        new() { Kind = kind, Host = host, Token = token };

    [Fact]
    public void LegacySlots_AreReadAsListEntries_WithoutTouchingTheSecret()
    {
        var data = Secret(
            ("gitlab_token", "glpat-old"), ("gitlab_host", "gitlab.example.com"),
            ("github_token", "ghp_old"));

        var entries = GitPatStore.Read(data);

        Assert.Collection(entries,
            e => { Assert.Equal(GitPatStore.LegacyGitLabId, e.Id); Assert.Equal("gitlab", e.Kind); Assert.Equal("gitlab.example.com", e.Host); Assert.Equal("glpat-old", e.Token); },
            // Stored before hosts existed: it was used against the public instance, so that is
            // the host it migrates with.
            e => { Assert.Equal(GitPatStore.LegacyGitHubId, e.Id); Assert.Equal("github", e.Kind); Assert.Equal("github.com", e.Host); });
        // Reading is not a write: an older backend must still understand the secret.
        Assert.True(data.ContainsKey("gitlab_token"));
        Assert.False(data.ContainsKey(GitPatStore.Key));
    }

    [Fact]
    public void LegacyIdsAreStable_SoADeleteSeenOnTheStatusPageStillHits()
    {
        // The status page shows ids before any write has happened. A random id per read would
        // make the DELETE for it miss on the next read.
        var data = Secret(("gitlab_token", "glpat-old"), ("gitlab_host", "gitlab.example.com"));
        var id = GitPatStore.Read(data).Single().Id;

        Assert.Equal(id, GitPatStore.Read(data).Single().Id);
        Assert.True(GitPatStore.Remove(data, id));
        Assert.Empty(GitPatStore.Read(data));
        Assert.False(data.ContainsKey("gitlab_token"));
        Assert.False(data.ContainsKey("gitlab_host"));
    }

    [Fact]
    public void TheNextWrite_FoldsLegacySlotsIntoTheListAndDropsThem()
    {
        var legacy = Secret(("gitlab_token", "glpat-old"), ("gitlab_host", "gitlab.example.com"));

        var secret = CredentialSecretFactory.CreateGeneralSecret("creds", "ns", "u", legacy,
            new UserCredentials { AnthropicApiKey = "sk-ant-x" });

        Assert.False(secret.Data.ContainsKey("gitlab_token"));
        Assert.False(secret.Data.ContainsKey("gitlab_host"));
        var entry = GitPatStore.Read(secret.Data).Single();
        Assert.Equal(("gitlab", "gitlab.example.com", "glpat-old"), (entry.Kind, entry.Host, entry.Token));
        Assert.Equal("sk-ant-x", Encoding.UTF8.GetString(secret.Data["anthropic_api_key"]));
    }

    [Fact]
    public void AListEntryForTheSameHost_WinsOverTheLegacySlot()
    {
        // The list entry was written after the migration started, so it is the newer token.
        var data = Secret(("gitlab_token", "glpat-old"), ("gitlab_host", "gitlab.example.com"));
        GitPatStore.Upsert(data, Request("gitlab", "gitlab.example.com", "glpat-new"));

        var entry = GitPatStore.Read(data).Single();
        Assert.Equal("glpat-new", entry.Token);
        Assert.False(data.ContainsKey("gitlab_token"));
    }

    [Fact]
    public void Upsert_AddsOneEntryPerHost()
    {
        var data = Secret();
        var a = GitPatStore.Upsert(data, Request("gitlab", "gitlab.example.com", "glpat-a"));
        var b = GitPatStore.Upsert(data, Request("gitlab", "gitlab.com", "glpat-b"));
        var c = GitPatStore.Upsert(data, Request("github", "github.com", "ghp_c"));

        Assert.Equal(3, new[] { a.Id, b.Id, c.Id }.Distinct().Count());
        Assert.Equal(["gitlab.example.com", "gitlab.com", "github.com"], GitPatStore.Read(data).Select(e => e.Host));
    }

    [Fact]
    public void Upsert_ForAStoredHost_RotatesTheTokenAndKeepsTheId()
    {
        // git's store helper answers with the first entry matching the host; a second line for
        // the same host would leave the stale token winning after a rotation.
        var data = Secret();
        var first = GitPatStore.Upsert(data, Request("gitlab", "gitlab.example.com", "glpat-old"));
        var second = GitPatStore.Upsert(data, Request("gitlab", " GitLab.Example.com ", "glpat-new"));

        Assert.Equal(first.Id, second.Id);
        var entry = Assert.Single(GitPatStore.Read(data));
        Assert.Equal("glpat-new", entry.Token);
        Assert.Equal("gitlab.example.com", entry.Host);
    }

    [Fact]
    public void Remove_IsIdempotent()
    {
        var data = Secret();
        var entry = GitPatStore.Upsert(data, Request("github", "github.com", "ghp_x"));
        GitPatStore.Upsert(data, Request("gitlab", "gitlab.com", "glpat-y"));

        Assert.True(GitPatStore.Remove(data, entry.Id));
        Assert.False(GitPatStore.Remove(data, entry.Id));
        Assert.False(GitPatStore.Remove(data, "never-existed"));
        Assert.Equal(["gitlab.com"], GitPatStore.Read(data).Select(e => e.Host));
    }

    [Fact]
    public void RemovingTheLastEntry_DropsTheKeyEntirely()
    {
        var data = Secret();
        var entry = GitPatStore.Upsert(data, Request("github", "github.com", "ghp_x"));
        GitPatStore.Remove(data, entry.Id);
        Assert.False(data.ContainsKey(GitPatStore.Key));
    }

    [Theory]
    [InlineData("bitbucket", "git.example.com", "tok")]
    [InlineData("", "git.example.com", "tok")]
    [InlineData(null, "git.example.com", "tok")]
    [InlineData("gitlab", null, "tok")]
    [InlineData("gitlab", "", "tok")]
    [InlineData("gitlab", "https://gitlab.example.com", "tok")]
    [InlineData("gitlab", "gitlab.example.com/group", "tok")]
    [InlineData("gitlab", "evil@gitlab.example.com", "tok")]
    [InlineData("gitlab", "gitlab.example.com", null)]
    [InlineData("gitlab", "gitlab.example.com", "")]
    [InlineData("gitlab", "gitlab.example.com", "has space")]
    [InlineData("gitlab", "gitlab.example.com", "glpat\nhttps://oauth2:x@evil.example")]
    public void Upsert_RejectsAnInvalidKindHostOrToken(string? kind, string? host, string? token)
    {
        var data = Secret();
        Assert.Throws<ArgumentException>(() => GitPatStore.Upsert(data, Request(kind!, host!, token!)));
        Assert.Empty(data);
    }

    [Fact]
    public void Upsert_RejectsATokenLongerThanTheCap()
    {
        var data = Secret();
        Assert.Throws<ArgumentException>(() => GitPatStore.Upsert(data,
            Request("github", "github.com", new string('x', ManualGitCredentials.MaxTokenLength + 1))));
    }

    [Fact]
    public void Upsert_IsBoundedSoTheSecretCannotGrowUntilWritesFail()
    {
        var data = Secret();
        for (var i = 0; i < GitPatStore.MaxEntries; i++)
            GitPatStore.Upsert(data, Request("gitlab", $"host-{i}.example.com", $"tok{i}"));

        Assert.Throws<ArgumentException>(() => GitPatStore.Upsert(data, Request("gitlab", "one-more.example.com", "tok")));
        // Rotation of an existing host is still allowed at the cap.
        GitPatStore.Upsert(data, Request("gitlab", "host-0.example.com", "rotated"));
        Assert.Equal(GitPatStore.MaxEntries, GitPatStore.Read(data).Count);
    }

    [Fact]
    public void Status_ListsIdKindAndHost_NeverTheToken()
    {
        var data = Secret(("gitlab_token", "glpat-legacy"), ("gitlab_host", "gitlab.example.com"));
        GitPatStore.Upsert(data, Request("github", "github.com", "ghp_listed"));

        var status = CredentialSecretFactory.CredentialStatus(data);

        Assert.Equal(2, status.GitPats.Count);
        Assert.Contains(status.GitPats, p => p.Kind == "gitlab" && p.Host == "gitlab.example.com");
        Assert.Contains(status.GitPats, p => p.Kind == "github" && p.Host == "github.com");
        // The serialized answer is what a browser receives; the token must not be anywhere in it.
        var json = JsonSerializer.Serialize(status);
        Assert.DoesNotContain("glpat-legacy", json);
        Assert.DoesNotContain("ghp_listed", json);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ACorruptList_IsReadAsEmptyRatherThanBreakingTheSession()
    {
        var data = Secret((GitPatStore.Key, "{not json"));
        Assert.Empty(GitPatStore.Read(data));
        Assert.Empty(ManualGitCredentials.Lines(data));
    }

    [Fact]
    public void TheSecretFactory_ProducesTheSameOwnerLabelledSecretForPatWrites()
    {
        var (secret, info) = CredentialSecretFactory.UpsertGitPat("creds-u", "ns", "owner", null,
            Request("github", "github.com", "ghp_x"));

        Assert.Equal("creds-u", secret.Metadata.Name);
        Assert.Equal("owner", secret.Metadata.Labels["agenthub.dev/owner"]);
        Assert.Equal(("github", "github.com"), (info.Kind, info.Host));

        var removed = CredentialSecretFactory.RemoveGitPat("creds-u", "ns", "owner", secret.Data, info.Id);
        Assert.False(removed.Data.ContainsKey(GitPatStore.Key));
    }
}
