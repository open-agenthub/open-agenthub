using AgentHub.Api.Models;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>What a stored token restriction may say, checked at the edge the user is looking at.</summary>
public sealed class ApiTokenScopeTests
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<ProviderAccountInfo>> Accounts =
        new Dictionary<string, IReadOnlyList<ProviderAccountInfo>>
        {
            ["Claude"] = [new("work0001", "Work", null, null, DateTime.UtcNow, null, true)],
            ["Codex"] = []
        };

    private static readonly IReadOnlyList<GitPatInfo> Pats = [new("pat-a", "gitlab", "gitlab.example.com")];

    [Fact]
    public void Normalize_CanonicalisesAgentNames_AndKeepsKnownIdsAndWildcards()
    {
        var scope = new ApiTokenScope
        {
            ProviderAccounts = new() { ["claude"] = [" work0001 ", "work0001"], ["CODEX"] = ["*", "ignored-after-wildcard"] },
            GitPats = ["pat-a"],
            ApiKeys = false
        };

        var normalized = ApiTokenScope.Normalize(scope, Accounts, Pats);

        Assert.Equal(["work0001"], normalized.ProviderAccounts!["Claude"]);
        Assert.Equal(["*"], normalized.ProviderAccounts["Codex"]);
        Assert.Equal(["pat-a"], normalized.GitPats);
        // "false" and "not given" mean the same thing and are stored the same way.
        Assert.Null(normalized.ApiKeys);
        Assert.False(normalized.AllowsApiKeys);
    }

    [Fact]
    public void Normalize_RefusesAnUnknownAccountPatOrAgent_NamingIt()
    {
        var badAccount = new ApiTokenScope { ProviderAccounts = new() { ["Claude"] = ["nope0000"] } };
        var badPat = new ApiTokenScope { GitPats = ["nope"] };
        var badAgent = new ApiTokenScope { ProviderAccounts = new() { ["Gemini"] = ["*"] } };

        Assert.Contains("nope0000", Assert.Throws<ArgumentException>(() => ApiTokenScope.Normalize(badAccount, Accounts, Pats)).Message);
        Assert.Contains("nope", Assert.Throws<ArgumentException>(() => ApiTokenScope.Normalize(badPat, Accounts, Pats)).Message);
        Assert.Contains("Gemini", Assert.Throws<ArgumentException>(() => ApiTokenScope.Normalize(badAgent, Accounts, Pats)).Message);
    }

    [Fact]
    public void Queries_TreatMissingPartsAsNotAllowed()
    {
        var scope = new ApiTokenScope { ProviderAccounts = new() { ["Claude"] = ["work0001"] } };

        Assert.True(scope.AllowsAccount(AgentKind.Claude, "work0001"));
        Assert.False(scope.AllowsAccount(AgentKind.Claude, "other"));
        Assert.Null(scope.AccountsFor(AgentKind.Codex));
        Assert.False(scope.AllowsGitPat("pat-a"));
        Assert.False(scope.AllowsAllGitPats);
        Assert.False(scope.AllowsApiKeys);
    }

    [Fact]
    public void Json_RoundTrips_AndGarbageIsTheMostRestrictiveScopeNotUnrestricted()
    {
        var scope = new ApiTokenScope
        {
            ProviderAccounts = new() { ["Claude"] = ["work0001"] }, GitPats = ["*"], ApiKeys = true
        };

        var json = scope.ToJson();
        var back = ApiTokenScope.FromJson(json)!;

        Assert.Contains("\"providerAccounts\":{\"Claude\":[\"work0001\"]}", json);
        Assert.Equal(["work0001"], back.ProviderAccounts!["Claude"]);
        Assert.True(back.AllowsAllGitPats);
        Assert.True(back.AllowsApiKeys);

        Assert.Null(ApiTokenScope.FromJson(null));
        Assert.Null(ApiTokenScope.FromJson(" "));
        // A column that cannot be read must not widen what the token can do.
        var garbage = ApiTokenScope.FromJson("{not json")!;
        Assert.Null(garbage.AccountsFor(AgentKind.Claude));
        Assert.False(garbage.AllowsAllGitPats);
    }
}
