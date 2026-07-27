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
        public bool HasMappings { get; set; }
        public Task<bool> IsAdminAsync(string owner, CancellationToken ct = default)
            => Task.FromResult(Admins.Contains(owner));
        public Task<bool> HasAdminMappingsAsync(CancellationToken ct = default)
            => Task.FromResult(HasMappings);
    }

    private static AdminAccess Build(string? admins, params IAdminRoleProvider[] providers)
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new[] { new KeyValuePair<string, string?>("Ee:Admins", admins) })
            .Build();
        return new AdminAccess(cfg, NullLogger<AdminAccess>.Instance, providers);
    }

    [Fact]
    public async Task EmptyList_IsBootstrap_EveryoneIsAdmin()
    {
        var a = Build("");
        Assert.True(a.Bootstrap);
        Assert.True(await a.IsAdminAsync("anyone"));
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
}
