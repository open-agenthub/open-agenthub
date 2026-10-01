using AgentHub.Api.Models;
using Xunit;

namespace AgentHub.Api.Tests;

public class SessionReposValidationTests
{
    private static List<RepoRef> Repos(params string[] urls) =>
        urls.Select(u => new RepoRef { Url = u }).ToList();

    [Fact]
    public void TheOrdinaryCases_StayAccepted()
    {
        // The validation is new, so anything that worked before must keep working.
        SessionRepos.Validate(Repos(
            "https://git.example.com/org/thing.git",
            "http://git.example.com/org/thing.git",
            "ssh://git@git.example.com/org/thing.git",
            "git://git.example.com/org/thing.git",
            "git@git.example.com:org/thing.git"));
        SessionRepos.Validate([]);
    }

    [Fact]
    public void ATransportThatRunsACommand_IsRefused()
    {
        // ext:: makes git execute an arbitrary command as its transport helper, and file:// reads
        // paths inside the pod rather than a repository. Neither is a repository a caller names.
        Assert.Throws<ArgumentException>(() => SessionRepos.Validate(Repos("ext::sh -c 'id'")));
        Assert.Throws<ArgumentException>(() => SessionRepos.Validate(Repos("file:///etc")));
        Assert.Throws<ArgumentException>(() => SessionRepos.Validate(Repos("/workspace/other")));
        Assert.Throws<ArgumentException>(() => SessionRepos.Validate(Repos("not a url")));
    }

    [Fact]
    public void AUrlThatWouldReachGitAsAnOption_IsRefused()
    {
        // The clone script quotes the URL, so this is not shell injection — --upload-pack is git's
        // own option and would run a command of the caller's choosing.
        Assert.Throws<ArgumentException>(() =>
            SessionRepos.Validate(Repos("--upload-pack=touch /tmp/x")));
    }

    [Fact]
    public void TooManyRepositories_AreRefusedBeforeAPodIsBuilt()
    {
        var tooMany = Repos(Enumerable.Range(0, SessionRepos.MaxCount + 1)
            .Select(i => $"https://git.example.com/org/thing-{i}.git").ToArray());

        var error = Assert.Throws<ArgumentException>(() => SessionRepos.Validate(tooMany));
        Assert.Contains(SessionRepos.MaxCount.ToString(), error.Message);

        // Exactly at the cap is fine.
        SessionRepos.Validate(Repos(Enumerable.Range(0, SessionRepos.MaxCount)
            .Select(i => $"https://git.example.com/org/thing-{i}.git").ToArray()));
    }

    [Fact]
    public void AnOverlongUrlOrBranch_IsRefused()
    {
        var longUrl = "https://git.example.com/" + new string('a', SessionRepos.MaxUrlLength);
        Assert.Throws<ArgumentException>(() => SessionRepos.Validate(Repos(longUrl)));

        Assert.Throws<ArgumentException>(() => SessionRepos.Validate([
            new RepoRef
            {
                Url = "https://git.example.com/org/thing.git",
                Branch = new string('b', SessionRepos.MaxBranchLength + 1)
            }
        ]));
    }

    [Fact]
    public void TheErrorNamesTheOffendingUrlWithoutPastingAnUnboundedString()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            SessionRepos.Validate(Repos("weird://" + new string('x', 400))));

        Assert.Contains("weird://", error.Message);
        Assert.True(error.Message.Length < 300);
    }
}
