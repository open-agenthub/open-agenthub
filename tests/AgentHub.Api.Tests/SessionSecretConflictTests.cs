using System.Text;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>
/// Two stored secrets may share a name; one session may not receive two that land in the same
/// place (#253, docs/session-secrets.md).
/// </summary>
public class SessionSecretConflictTests
{
    private static readonly DateTime Now = new(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);

    private static readonly IReadOnlyList<SessionSecretStore.Entry> Stored =
    [
        new("prod", "API_KEY", "env", "production", Now),
        new("stag", "API_KEY", "env", "staging", Now),
        new("file", "API_KEY", "file", null, Now),
        new("lower", "api_key", "env", null, Now),
        new("kube1", "kubeconfig", "file", null, Now),
        new("kube2", "kubeconfig", "file", null, Now)
    ];

    private static SessionRecord Session(string id, string? project = null, string? secretIds = null, string? grants = null,
        string? parent = null) => new()
    {
        Id = id, Owner = "alice", Title = $"title-{id}", ProjectId = project, SecretIdsJson = secretIds,
        SecretGrantIdsJson = grants, ParentSessionId = parent, AgentSessionId = "a", CallbackToken = "t"
    };

    private static void Check(IEnumerable<SessionRecord> sessions,
        Dictionary<string, (string Name, IReadOnlyList<string> SecretIds)>? projects = null, string? self = null) =>
        SessionSecretConflicts.EnsureNone(Stored, sessions.ToDictionary(s => s.Id), projects ?? [], selfSessionId: self);

    [Fact]
    public void SameNameButDifferentDeliveryOrCase_DoesNotCollide()
    {
        // A variable and a file live in different copies; API_KEY and api_key are two variables.
        Assert.Null(SessionSecretConflicts.Find(Stored.Where(e => e.Id is "prod" or "file" or "lower")));
    }

    [Fact]
    public void SameNameAndDelivery_Collides_AndTheMessageSaysWhere()
    {
        var c = Assert.IsType<SessionSecretConflicts.Conflict>(SessionSecretConflicts.Find(Stored));
        Assert.Equal(("env", "API_KEY"), (c.Delivery, c.Name));
        Assert.Equal(["prod", "stag"], c.Ids);

        var e = Assert.Throws<SessionSecretConflictException>(() => Check([Session("s1", secretIds: """["kube1","kube2"]""")], self: "s1"));
        Assert.Contains("This session would receive 2 secrets as the file ~/.secrets/kubeconfig (ids kube1, kube2)", e.Message);
        Assert.IsAssignableFrom<ArgumentException>(e);
    }

    [Fact]
    public void AProjectSelectingBoth_IsRefused_EvenWithoutSessions()
    {
        var e = Assert.Throws<SessionSecretConflictException>(() => Check([],
            new() { ["p"] = ("Deploys", ["prod", "stag"]) }));
        Assert.Contains("The project “Deploys” would select 2 secrets as the environment variable API_KEY", e.Message);
    }

    [Fact]
    public void ProjectSelectionPlusAGrant_IsAConflict_ForTheSession()
    {
        var projects = new Dictionary<string, (string, IReadOnlyList<string>)> { ["p"] = ("Deploys", ["prod"]) };
        Check([Session("s1", "p")], projects);
        var e = Assert.Throws<SessionSecretConflictException>(() => Check(
            [Session("s1", "p", grants: """[{"SecretId":"stag","InheritToChildren":false,"Source":"granted"}]""")], projects));
        Assert.Contains("The session “title-s1” (s1) would receive", e.Message);
    }

    [Fact]
    public void AnInheritedGrant_CountsLikeAnyOther()
    {
        var parent = Session("parent", secretIds: "[]",
            grants: """[{"SecretId":"stag","InheritToChildren":true,"Source":"granted"}]""");
        var child = Session("child", secretIds: """["prod"]""", parent: "parent",
            grants: """[{"SecretId":"stag","InheritToChildren":true,"Source":"inherited","SourceSessionId":"parent"}]""");
        var e = Assert.Throws<SessionSecretConflictException>(() => Check([parent, child]));
        Assert.Contains("(child)", e.Message);
    }

    [Fact]
    public void OnlyTheNamedSessionsAndProjectsAreChecked()
    {
        var sessions = new[] { Session("bad", secretIds: """["prod","stag"]"""), Session("ok", secretIds: """["prod"]""") }
            .ToDictionary(s => s.Id);
        SessionSecretConflicts.EnsureNone(Stored, sessions, new Dictionary<string, (string, IReadOnlyList<string>)>(),
            onlySessions: ["ok"], onlyProjects: []);
        Assert.Throws<SessionSecretConflictException>(() => SessionSecretConflicts.EnsureNone(Stored, sessions,
            new Dictionary<string, (string, IReadOnlyList<string>)>()));
    }

    [Fact]
    public void Delivery_WithholdsBothSidesOfAClash_InsteadOfLettingTheLastOneWin()
    {
        var data = new Dictionary<string, byte[]>();
        var prod = SessionSecretStore.Upsert(data, new("API_KEY", "env", "prod-value-1", null), Now);
        var stag = SessionSecretStore.Upsert(data, new("API_KEY", "env", "staging-value-2", null), Now);
        var other = SessionSecretStore.Upsert(data, new("OTHER", "env", "other-value-3", null), Now);

        var delivery = SessionSecretDeliveryFactory.Build("sid", "ns", new Dictionary<string, string>(), data,
            [prod.Id, stag.Id, other.Id]);

        Assert.Equal(["OTHER"], delivery.EnvNames);
        Assert.Equal(2, delivery.Withheld);
        Assert.Equal("other-value-3", Encoding.UTF8.GetString(delivery.Env.Data["OTHER"]));
    }
}
