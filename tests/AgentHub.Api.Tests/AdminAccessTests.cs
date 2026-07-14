using AgentHub.Api.Admin;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentHub.Api.Tests;

public class AdminAccessTests
{
    private static AdminAccess Build(string? admins)
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new[] { new KeyValuePair<string, string?>("Ee:Admins", admins) })
            .Build();
        return new AdminAccess(cfg, NullLogger<AdminAccess>.Instance);
    }

    [Fact]
    public void EmptyList_IsBootstrap_EveryoneIsAdmin()
    {
        var a = Build("");
        Assert.True(a.Bootstrap);
        Assert.True(a.IsAdmin("anyone"));
        Assert.False(a.IsAdmin(null));
        Assert.False(a.IsAdmin(""));
    }

    [Fact]
    public void ConfiguredList_OnlyListedAreAdmins()
    {
        var a = Build("alice, bob;carol");
        Assert.False(a.Bootstrap);
        Assert.True(a.IsAdmin("alice"));
        Assert.True(a.IsAdmin("bob"));
        Assert.True(a.IsAdmin("carol"));
        Assert.False(a.IsAdmin("mallory"));
    }

    [Fact]
    public void AdminMatch_IsCaseInsensitive()
    {
        var a = Build("Alice");
        Assert.True(a.IsAdmin("alice"));
        Assert.True(a.IsAdmin("ALICE"));
    }
}
