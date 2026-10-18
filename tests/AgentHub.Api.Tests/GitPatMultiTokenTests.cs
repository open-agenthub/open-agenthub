using System.Text;
using System.Text.Json;
using AgentHub.Api.Models;
using AgentHub.Api.Services;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>
/// Several git tokens for one host (issue #132): a personal token next to a group or project token,
/// each with a label and optionally an author, one of them the host's default — and a session that
/// never gets more than one per host, because git's store answers with the first line for a host.
/// </summary>
public class GitPatMultiTokenTests
{
    private const string Host = "gitlab.example.com";

    private static (Dictionary<string, byte[]> Data, GitPatStore.Entry Personal, GitPatStore.Entry Group) TwoForOneHost()
    {
        var data = new Dictionary<string, byte[]>();
        var personal = GitPatStore.Upsert(data, new UpsertGitPatRequest { Kind = "gitlab", Host = Host, Token = "glpat-personal", Label = "personal" });
        var group = GitPatStore.Upsert(data, new UpsertGitPatRequest
        {
            Kind = "gitlab", Host = Host, Token = "glpat-group", Label = "group: platform",
            AuthorName = "Platform Bot", AuthorEmail = "platform-bot@example.com"
        });
        return (data, personal, group);
    }

    [Fact]
    public void ASecondLabelForAHost_IsASecondToken_AndTheFirstStaysTheDefault()
    {
        var (data, personal, group) = TwoForOneHost();

        var stored = GitPatStore.Read(data);
        Assert.Equal(2, stored.Count);
        Assert.NotEqual(personal.Id, group.Id);
        Assert.True(stored.Single(e => e.Id == personal.Id).Default);
        Assert.False(stored.Single(e => e.Id == group.Id).Default);
    }

    [Fact]
    public void SameHostAndLabel_RotatesInPlace_SoAClientWithoutLabelsKeepsItsBehaviour()
    {
        var (data, personal, _) = TwoForOneHost();

        var rotated = GitPatStore.Upsert(data, new UpsertGitPatRequest { Kind = "gitlab", Host = Host, Token = "glpat-new", Label = "Personal" });

        Assert.Equal(personal.Id, rotated.Id);
        Assert.Equal(2, GitPatStore.Read(data).Count);
        Assert.Equal("glpat-new", GitPatStore.Read(data).Single(e => e.Id == personal.Id).Token);
    }

    [Fact]
    public void AnIdEdit_ChangesLabelAuthorAndDefault_WithoutResendingTheToken()
    {
        var (data, personal, group) = TwoForOneHost();

        GitPatStore.Upsert(data, new UpsertGitPatRequest { Id = group.Id, Default = true, Label = "group" });

        var stored = GitPatStore.Read(data);
        var edited = stored.Single(e => e.Id == group.Id);
        Assert.True(edited.Default);
        Assert.Equal("group", edited.Label);
        Assert.Equal("glpat-group", edited.Token);
        Assert.Equal("Platform Bot", edited.AuthorName);
        Assert.False(stored.Single(e => e.Id == personal.Id).Default);

        // An empty string clears an optional field; a missing one keeps it.
        GitPatStore.Upsert(data, new UpsertGitPatRequest { Id = group.Id, AuthorName = "" });
        Assert.Null(GitPatStore.Read(data).Single(e => e.Id == group.Id).AuthorName);
        Assert.Equal("platform-bot@example.com", GitPatStore.Read(data).Single(e => e.Id == group.Id).AuthorEmail);
    }

    [Fact]
    public void AnIdEdit_CannotMoveATokenToAnotherHost()
    {
        var (data, personal, _) = TwoForOneHost();
        // A session's selection is judged per host; moving the id would move every session with it.
        Assert.Throws<ArgumentException>(() =>
            GitPatStore.Upsert(data, new UpsertGitPatRequest { Id = personal.Id, Host = "gitlab.other.example" }));
        Assert.Throws<ArgumentException>(() =>
            GitPatStore.Upsert(data, new UpsertGitPatRequest { Id = "missing", Label = "x" }));
    }

    [Fact]
    public void Default_CannotBeSwitchedOff_OnlyHandedToAnotherToken()
    {
        var (data, personal, _) = TwoForOneHost();
        GitPatStore.Upsert(data, new UpsertGitPatRequest { Id = personal.Id, Default = false });
        Assert.True(GitPatStore.Read(data).Single(e => e.Id == personal.Id).Default);
    }

    [Fact]
    public void RemovingTheDefault_HandsTheRoleToTheNextTokenOfThatHost_InTheStoredDocument()
    {
        var (data, personal, group) = TwoForOneHost();

        GitPatStore.Remove(data, personal.Id);

        var raw = JsonDocument.Parse(data[GitPatStore.Key]).RootElement;
        Assert.True(raw[0].GetProperty("default").GetBoolean());
        Assert.Equal(group.Id, GitPatStore.Read(data).Single(e => e.Default).Id);
    }

    [Fact]
    public void Migration_EveryExistingEntryKeepsItsIdAndBecomesItsHostsDefault()
    {
        // The document an older backend wrote: one entry per host, no label, no default flag.
        var data = new Dictionary<string, byte[]>
        {
            [GitPatStore.Key] = Encoding.UTF8.GetBytes(
                """[{"id":"abc","kind":"gitlab","host":"gitlab.example.com","token":"glpat-a"},{"id":"def","kind":"github","host":"github.com","token":"ghp_b"}]"""),
            ["gitlab_token"] = Encoding.UTF8.GetBytes("glpat-legacy"),
            ["gitlab_host"] = Encoding.UTF8.GetBytes("gitlab.other.example")
        };

        var stored = GitPatStore.Read(data);

        Assert.Equal(["abc", "def", GitPatStore.LegacyGitLabId], stored.Select(e => e.Id));
        Assert.All(stored, e => Assert.True(e.Default));
        // And a session that never chose gets exactly what it got before.
        Assert.Equal(stored, GitPatSelection.Apply(stored, null));
    }

    [Fact]
    public void ASessionWithoutAChoice_GetsTheDefaultOfEachHost_NotEveryToken()
    {
        var (data, personal, _) = TwoForOneHost();
        GitPatStore.Upsert(data, new UpsertGitPatRequest { Kind = "github", Host = "github.com", Token = "ghp_x" });
        var stored = GitPatStore.Read(data);

        var lines = ManualGitCredentials.Lines(GitPatSelection.Apply(stored, null));

        Assert.Equal(["https://oauth2:glpat-personal@gitlab.example.com", "https://x-access-token:ghp_x@github.com"], lines);
        Assert.DoesNotContain(lines, l => l.Contains("glpat-group"));
        Assert.Contains(personal.Id, GitPatSelection.Apply(stored, null).Select(e => e.Id));
    }

    [Fact]
    public void ASelectionWithTwoTokensForOneHost_IsRefusedWhenWritten()
    {
        var (data, personal, group) = TwoForOneHost();
        var stored = GitPatStore.Read(data);

        var error = Assert.Throws<ArgumentException>(() => GitPatSelection.Normalize([personal.Id, group.Id], stored));
        Assert.Contains(Host, error.Message);
        Assert.Equal([group.Id], GitPatSelection.Normalize([group.Id, group.Id], stored));
    }

    [Fact]
    public void AStoredSelectionWithTwoTokensForOneHost_StillYieldsOneLine()
    {
        // Written before the rule existed (or by hand): the pod must still get one line per host.
        var (data, personal, group) = TwoForOneHost();
        var applied = GitPatSelection.Apply(GitPatStore.Read(data), [group.Id, personal.Id]);
        Assert.Single(applied);
        Assert.Equal(personal.Id, applied[0].Id);
    }

    [Fact]
    public void ARemovedChosenToken_DoesNotFallBackToTheOwnersDefault()
    {
        var (data, _, group) = TwoForOneHost();
        GitPatStore.Remove(data, group.Id);
        Assert.Empty(GitPatSelection.Apply(GitPatStore.Read(data), [group.Id]));
    }

    [Fact]
    public void Authors_FollowTheChosenToken_AndSkipAHostTheConnectedProviderCovers()
    {
        var (data, _, group) = TwoForOneHost();
        var chosen = GitPatSelection.Apply(GitPatStore.Read(data), [group.Id]);

        Assert.Equal([new GitAuthor(Host, "Platform Bot", "platform-bot@example.com")], ManualGitCredentials.Authors(chosen, null));
        // The OAuth line comes first in the store and is what pushes authenticate with, so the
        // PAT's author would claim an identity the push does not use.
        Assert.Empty(ManualGitCredentials.Authors(chosen, "https://oauth2:oauth-token@gitlab.example.com\n"));
    }

    [Fact]
    public void ThePodPayload_IsWhatTheRuntimeParses()
    {
        var git = new SessionGitCredentials("https://oauth2:t@gitlab.example.com\n",
            [new GitAuthor(Host, "Platform Bot", null)]);
        var json = JsonDocument.Parse(git.PodPayload()).RootElement;
        Assert.Equal("https://oauth2:t@gitlab.example.com\n", json.GetProperty("credentials").GetString());
        Assert.Equal(Host, json.GetProperty("authors")[0].GetProperty("host").GetString());
        Assert.False(json.GetProperty("authors")[0].TryGetProperty("email", out _));
    }

    [Theory]
    [InlineData("Bot\" \n[core]", null)]
    [InlineData("Bot <x>", null)]
    [InlineData(null, "two words@example.com")]
    [InlineData(null, "no-at-sign")]
    [InlineData(null, "a\"@example.com")]
    public void Authors_ThatWouldBreakTheGitConfigOrTheCommitLine_AreRefused(string? name, string? email)
    {
        var data = new Dictionary<string, byte[]>();
        Assert.Throws<ArgumentException>(() => GitPatStore.Upsert(data, new UpsertGitPatRequest
        {
            Kind = "gitlab", Host = Host, Token = "glpat-x", AuthorName = name, AuthorEmail = email
        }));
    }

    [Fact]
    public void TheListing_ShowsLabelDefaultAndAuthor_NeverTheToken()
    {
        var (data, personal, group) = TwoForOneHost();
        var status = CredentialSecretFactory.CredentialStatus(data);
        var json = JsonSerializer.Serialize(status.GitPats);

        Assert.DoesNotContain("glpat-", json);
        Assert.Equal(new GitPatInfo(group.Id, "gitlab", Host, "group: platform", false, "Platform Bot", "platform-bot@example.com"),
            status.GitPats.Single(p => p.Id == group.Id));
        Assert.True(status.GitPats.Single(p => p.Id == personal.Id).IsDefault);
    }

    [Fact]
    public void ARestrictedApiToken_AllowedBothTokensOfAHost_GetsOne_PreferringTheDefault()
    {
        GitPatInfo[] pats =
        [
            new("personal", "gitlab", Host, "personal", true),
            new("group", "gitlab", Host, "group", false),
            new("bot", "github", "github.com", "bot", false)
        ];
        var scope = new ApiTokenScope { ProviderAccounts = new() { ["claude"] = ["*"] }, GitPats = ["personal", "group", "bot"] };
        var request = new CreateSessionRequest { Title = "t", Agent = AgentKind.Claude, AuthMode = AgentAuthMode.Subscription };
        var accounts = new Dictionary<string, IReadOnlyList<ProviderAccountInfo>>();

        Assert.Equal(["personal", "bot"], CredentialScope.ApplyToCreate(request, scope, accounts, pats).GitPatIds);

        // Allowed only the group token: that one, even though it is not the owner's default.
        var groupOnly = scope with { GitPats = ["group"] };
        Assert.Equal(["group"], CredentialScope.ApplyToCreate(request, groupOnly, accounts, pats).GitPatIds);
    }
}
