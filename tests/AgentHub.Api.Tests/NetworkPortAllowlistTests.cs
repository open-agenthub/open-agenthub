using AgentHub.Api.Network;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentHub.Api.Tests;

public class NetworkPortAllowlistTests
{
    [Theory]
    [InlineData("5432", 5432, true)]
    [InlineData("5432", 5433, false)]
    [InlineData("9000-9100", 9000, true)]
    [InlineData("9000-9100", 9100, true)]
    [InlineData("9000-9100", 9101, false)]
    [InlineData("5432, 3306, 9000-9100", 3306, true)]
    [InlineData(" 5432 , 9000 - 9100 ", 9050, true)]
    public void Parse_AnswersMembership(string setting, int port, bool expected)
    {
        Assert.Equal(expected, NetworkPortAllowlist.Parse(setting).IsAllowed(port));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData(",,")]
    public void Parse_EmptySettingAllowsNothing(string? setting)
    {
        var allowlist = NetworkPortAllowlist.Parse(setting);
        Assert.True(allowlist.IsEmpty);
        Assert.False(allowlist.IsAllowed(80));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("100-1")]      // inverted range
    [InlineData("1-2-3")]
    [InlineData("-80")]
    public void Parse_DropsInvalidTokensFailClosed(string setting)
    {
        Assert.True(NetworkPortAllowlist.Parse(setting).IsEmpty);
    }

    [Fact]
    public void Parse_KeepsValidTokensNextToInvalidOnes()
    {
        var allowlist = NetworkPortAllowlist.Parse("garbage, 5432, 70000, 9000-9100");
        Assert.True(allowlist.IsAllowed(5432));
        Assert.True(allowlist.IsAllowed(9050));
        Assert.False(allowlist.IsAllowed(22));
    }

    [Fact]
    public void Options_BindFromConfiguration()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Network:Enabled"] = "false",
            ["Network:RequestablePorts"] = "80,443"
        }).Build();

        var options = cfg.GetSection("Network").Get<NetworkPortOptions>()!;

        Assert.False(options.Enabled);
        Assert.Equal("80,443", options.RequestablePorts);
        var allowlist = NetworkPortAllowlist.Parse(options.RequestablePorts);
        Assert.True(allowlist.IsAllowed(443));
        Assert.False(allowlist.IsAllowed(5432));
    }

    [Fact]
    public void Options_DefaultsCoverCommonDatabasesAndDevRange()
    {
        var options = new NetworkPortOptions();
        var allowlist = NetworkPortAllowlist.Parse(options.RequestablePorts);

        Assert.True(options.Enabled);
        foreach (var port in new[] { 5432, 3306, 6379, 27017, 9000, 9100 })
            Assert.True(allowlist.IsAllowed(port));
        Assert.False(allowlist.IsAllowed(22));
        Assert.False(allowlist.IsAllowed(6443));
    }

    [Fact]
    public void Options_EmptyConfiguredListOverridesTheDefault()
    {
        // The chart writes a single (possibly empty) string on purpose: an empty list
        // must yield an empty allowlist, not the compiled-in defaults.
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Network:RequestablePorts"] = ""
        }).Build();

        var options = cfg.GetSection("Network").Get<NetworkPortOptions>()!;
        Assert.True(NetworkPortAllowlist.Parse(options.RequestablePorts).IsEmpty);
    }
}
