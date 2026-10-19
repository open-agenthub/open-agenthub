using System.Text.Json;
using AgentHub.Api.Models;
using AgentHub.Api.Services;
using Xunit;

namespace AgentHub.Api.Tests;

public class SessionSecretGrantsTests
{
    [Fact]
    public void ExistingRequestedGrantsDoNotGainInheritanceOnUpgrade()
    {
        var grant = Assert.Single(SessionSecretGrants.Parse("[\"secret-a\"]"));
        Assert.Equal("requested", grant.Source);
        Assert.False(grant.InheritToChildren);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{\"SecretId\":\"a\"}")]
    [InlineData("broken")]
    public void UnreadableGrantStorageGivesNoAccess(string? json)
        => Assert.Empty(SessionSecretGrants.Parse(json));

    [Fact]
    public void PublicGrantCannotForgeProvenance()
    {
        var grant = JsonSerializer.Deserialize<SessionSecretAccess>(
            """{"secretId":"a","source":"inherited"}""", new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(new SessionSecretAccess("a", false), grant);
    }

    [Fact]
    public void RestrictedTokenChecksGrantsEvenWhenSelectionFollowsProject()
    {
        Assert.Throws<CredentialScopeException>(() => CredentialScope.ApplySecrets(
            new CreateSessionRequest { Title = "t", SecretGrants = [new("outside")] },
            new ApiTokenScope { Secrets = ["allowed"] }, []));
    }
}

public class SessionSecretAncestryTests
{
    [Fact]
    public void ParentRevocationReachesDescendantsButDirectChildGrantsSurvive()
    {
        var parent = Record("parent", [new("a", true, "granted")]);
        var child = Record("child", [new("a", true, "inherited", "parent")]);
        var grandchild = Record("grandchild", [new("a", true, "inherited", "child")]);
        var direct = Record("direct", [new("a", false, "granted")]);
        var resolver = new SessionSecretAccessResolver(new[] {parent, child, grandchild, direct}.ToDictionary(s => s.Id),
            new Dictionary<string, IReadOnlyList<string>>(), ["a"]);
        Assert.Equal(["a"], resolver.Effective(grandchild));
        parent.SecretGrantIdsJson = "[]";
        Assert.Empty(resolver.Effective(child));
        Assert.Empty(resolver.Effective(grandchild));
        Assert.Empty(resolver.ActiveGrants(child));
        Assert.Equal(["a"], resolver.Effective(direct));
    }

    [Fact]
    public void DisabledInheritanceOrMissingParentGivesNoAccess()
    {
        var parent = Record("parent", [new("a", false, "granted")]);
        var child = Record("child", [new("a", true, "inherited", "parent")]);
        var records = new Dictionary<string, AgentHub.Api.Persistence.SessionRecord> { [parent.Id] = parent, [child.Id] = child };
        var resolver = new SessionSecretAccessResolver(records, new Dictionary<string, IReadOnlyList<string>>(), ["a"]);
        Assert.Empty(resolver.Effective(child));
        records.Remove(parent.Id);
        Assert.Empty(resolver.Effective(child));
    }

    private static AgentHub.Api.Persistence.SessionRecord Record(string id, List<StoredSessionSecretGrant> grants)
        => new() { Id = id, Owner = "alice", Title = id, CallbackToken = "synthetic-token", SecretGrantIdsJson = SessionSecretGrants.Serialize(grants) };
}
