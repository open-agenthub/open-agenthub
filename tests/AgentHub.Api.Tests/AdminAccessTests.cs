using AgentHub.Api.Admin;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentHub.Api.Tests;

public class AdminAccessTests
{
    private sealed class FakeRoleProvider : IAdminRoleProvider
    {
        public HashSet<string> Admins { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Users { get; } = new(StringComparer.Ordinal);
        public bool HasMappings { get; set; }
        public Task<GroupRole?> GetGroupRoleAsync(string owner, CancellationToken ct = default)
            => Task.FromResult(
                Admins.Contains(owner) ? new GroupRole("admin", ["leads"])
                : Users.Contains(owner) ? new GroupRole("user", ["staff"])
                : null);
        public Task<bool> HasAdminMappingsAsync(CancellationToken ct = default)
            => Task.FromResult(HasMappings);
    }

    internal sealed class InMemoryUserRoleStore : IUserRoleStore
    {
        public Dictionary<string, string> Roles { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<RoleAuditEntry> Audit { get; } = new();

        public Task<string?> GetRoleAsync(string owner, CancellationToken ct = default)
            => Task.FromResult(Roles.TryGetValue(owner, out var r) ? r : null);
        public Task<IReadOnlyDictionary<string, string>> ListRolesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<string, string>>(
                new Dictionary<string, string>(Roles, StringComparer.OrdinalIgnoreCase));
        public Task<string?> SetRoleAsync(string owner, string? role, CancellationToken ct = default)
        {
            Roles.TryGetValue(owner, out var previous);
            if (role is null) Roles.Remove(owner); else Roles[owner] = role;
            return Task.FromResult(previous);
        }
        public Task<bool> AnyAdminAsync(CancellationToken ct = default)
            => Task.FromResult(Roles.Values.Contains("admin"));
        public Task AuditAsync(string actor, string subjectKind, string subject, string? oldRole, string? newRole,
            CancellationToken ct = default)
        {
            Audit.Add(new RoleAuditEntry(DateTime.UtcNow, actor, subjectKind, subject, oldRole, newRole));
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<RoleAuditEntry>> ListAuditAsync(int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RoleAuditEntry>>(Audit.AsEnumerable().Reverse().Take(limit).ToList());
        public Task<IAsyncDisposable> LockAsync(CancellationToken ct = default)
            => Task.FromResult<IAsyncDisposable>(new NoLock());
        private sealed class NoLock : IAsyncDisposable { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    }

    private static AdminAccess Build(string? admins, params IAdminRoleProvider[] providers)
        => Build(admins, null, providers);

    private static AdminAccess Build(string? admins, IUserRoleStore? roles, params IAdminRoleProvider[] providers)
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new[] { new KeyValuePair<string, string?>("Ee:Admins", admins) })
            .Build();
        return new AdminAccess(cfg, NullLogger<AdminAccess>.Instance, providers, roles);
    }

    [Fact]
    public async Task EmptyList_IsBootstrap_EveryoneIsAdmin()
    {
        var a = Build("");
        Assert.True(a.Bootstrap);
        Assert.True(await a.IsAdminAsync("anyone"));
        Assert.Equal(RoleSources.Bootstrap, (await a.ResolveAsync("anyone")).Source);
        Assert.False(await a.IsAdminAsync(null));
        Assert.False(await a.IsAdminAsync(""));
    }

    [Fact]
    public async Task ConfiguredList_OnlyListedAreAdmins()
    {
        var a = Build("alice, bob;carol");
        Assert.False(a.Bootstrap);
        Assert.True(await a.IsAdminAsync("alice"));
        Assert.True(await a.IsAdminAsync("bob"));
        Assert.True(await a.IsAdminAsync("carol"));
        Assert.False(await a.IsAdminAsync("mallory"));
        Assert.Equal(RoleSources.Default, (await a.ResolveAsync("mallory")).Source);
    }

    [Fact]
    public async Task AdminMatch_IsCaseInsensitive()
    {
        var a = Build("Alice");
        Assert.True(await a.IsAdminAsync("alice"));
        Assert.True(await a.IsAdminAsync("ALICE"));
    }

    [Fact]
    public async Task RoleProvider_GrantsAdmin_EvenWithConfiguredList()
    {
        var provider = new FakeRoleProvider { HasMappings = true };
        provider.Admins.Add("dora");
        var a = Build("alice", provider);
        Assert.True(await a.IsAdminAsync("dora"));
        Assert.Equal(new RoleResolution("admin", RoleSources.Group, ["leads"]), await a.ResolveAsync("dora"), new ResolutionComparer());
        Assert.True(await a.IsAdminAsync("alice"));
        Assert.False(await a.IsAdminAsync("mallory"));
    }

    [Fact]
    public async Task Bootstrap_IsDisabled_WhenARoleProviderHasAdminMappings()
    {
        var provider = new FakeRoleProvider { HasMappings = true };
        provider.Admins.Add("dora");
        var a = Build("", provider);
        Assert.True(a.Bootstrap); // config-derived flag stays, but…
        Assert.True(await a.IsAdminAsync("dora"));
        Assert.False(await a.IsAdminAsync("anyone")); // …everyone-is-admin no longer applies
    }

    [Fact]
    public async Task Bootstrap_StaysActive_WhileProviderHasNoMappings()
    {
        var provider = new FakeRoleProvider { HasMappings = false };
        var a = Build("", provider);
        Assert.True(await a.IsAdminAsync("anyone"));
    }

    [Fact]
    public async Task ExplicitAdmin_EndsBootstrap()
    {
        var roles = new InMemoryUserRoleStore();
        var a = Build("", roles);
        Assert.True(await a.IsAdminAsync("bob"));

        await roles.SetRoleAsync("alice", "admin");

        Assert.False(await a.IsBootstrapActiveAsync());
        Assert.Equal(RoleSources.User, (await a.ResolveAsync("alice")).Source);
        Assert.False(await a.IsAdminAsync("bob"));
    }

    [Fact]
    public async Task ExplicitUser_DoesNotEndBootstrap_ButAppliesToThatUser()
    {
        var roles = new InMemoryUserRoleStore();
        await roles.SetRoleAsync("bob", "user");
        var a = Build("", roles);

        Assert.True(await a.IsBootstrapActiveAsync());
        Assert.False(await a.IsAdminAsync("bob"));
        Assert.True(await a.IsAdminAsync("carol"));
    }

    [Fact]
    public async Task ExplicitUser_WinsOverAdminGroup()
    {
        var provider = new FakeRoleProvider { HasMappings = true };
        provider.Admins.Add("dora");
        var roles = new InMemoryUserRoleStore();
        await roles.SetRoleAsync("dora", "user");
        var a = Build("", roles, provider);

        var resolved = await a.ResolveAsync("dora");
        Assert.Equal("user", resolved.Role);
        Assert.Equal(RoleSources.User, resolved.Source);
    }

    [Fact]
    public async Task ExplicitAdmin_WinsOverUserGroup()
    {
        var provider = new FakeRoleProvider { HasMappings = true };
        provider.Users.Add("erin");
        var roles = new InMemoryUserRoleStore();
        await roles.SetRoleAsync("erin", "admin");
        var a = Build("", roles, provider);

        Assert.Equal(RoleSources.User, (await a.ResolveAsync("erin")).Source);
        Assert.True(await a.IsAdminAsync("erin"));
    }

    [Fact]
    public async Task ValuesAdmin_CannotBeDemotedByAnExplicitRole()
    {
        var roles = new InMemoryUserRoleStore();
        await roles.SetRoleAsync("alice", "user");
        var a = Build("alice", roles);

        var resolved = await a.ResolveAsync("ALICE");
        Assert.True(resolved.IsAdmin);
        Assert.Equal(RoleSources.Values, resolved.Source);
    }

    [Fact]
    public async Task UserGroup_OnlyLabels_TheRole()
    {
        var provider = new FakeRoleProvider { HasMappings = false };
        provider.Users.Add("frank");
        // Bootstrap still makes frank an admin: a user mapping is a label, not a restriction.
        Assert.Equal(RoleSources.Bootstrap, (await Build("", provider).ResolveAsync("frank")).Source);

        var resolved = await Build("alice", provider).ResolveAsync("frank");
        Assert.Equal("user", resolved.Role);
        Assert.Equal(RoleSources.Group, resolved.Source);
        Assert.Equal(["staff"], resolved.Groups);
    }

    [Fact]
    public async Task KeepsAnAdmin_CountsValuesAdminsAndKnownUsers()
    {
        Assert.True(await Build("alice").KeepsAnAdminAsync(bootstrapBefore: false, []));

        var roles = new InMemoryUserRoleStore();
        var a = Build("", roles);
        Assert.True(await a.KeepsAnAdminAsync(true, ["bob"]));       // bootstrap
        await roles.SetRoleAsync("bob", "user");
        Assert.False(await a.KeepsAnAdminAsync(true, ["bob"]));      // the only known user is demoted
        Assert.True(await a.KeepsAnAdminAsync(true, ["bob", "carol"]));
        await roles.SetRoleAsync("carol", "admin");
        Assert.True(await a.KeepsAnAdminAsync(true, ["bob", "carol"]));
    }

    [Fact]
    public async Task KeepsAnAdmin_IsFalse_WhenTheChangeReopensBootstrap()
    {
        var roles = new InMemoryUserRoleStore();
        await roles.SetRoleAsync("carol", "admin");
        var a = Build("", roles);
        Assert.False(await a.IsBootstrapActiveAsync());

        await roles.SetRoleAsync("carol", null);

        // Everyone would resolve to admin again — exactly what demoting the last admin must not do.
        Assert.True(await a.IsAdminAsync("carol"));
        Assert.False(await a.KeepsAnAdminAsync(bootstrapBefore: false, ["carol", "dave"]));
    }

    private sealed class ResolutionComparer : IEqualityComparer<RoleResolution>
    {
        public bool Equals(RoleResolution? x, RoleResolution? y)
            => x is not null && y is not null && x.Role == y.Role && x.Source == y.Source && x.Groups.SequenceEqual(y.Groups);
        public int GetHashCode(RoleResolution obj) => obj.Role.GetHashCode();
    }
}
