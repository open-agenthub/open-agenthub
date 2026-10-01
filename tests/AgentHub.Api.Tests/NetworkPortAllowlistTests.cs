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
    public void Options_DefaultsCoverBackingServicesAndTheUsualDevServers()
    {
        var options = new NetworkPortOptions();
        var allowlist = NetworkPortAllowlist.Parse(options.RequestablePorts);

        Assert.True(options.Enabled);
        // Backing services, including self-hosted object storage: without 3900 an agent
        // cannot ask to reach Garage, and the refusal never reaches the owner.
        foreach (var port in new[] { 3306, 3900, 5432, 6379, 9000, 9100, 27017 })
            Assert.True(allowlist.IsAllowed(port), $"backing service port {port}");
        // The dev server each framework starts by itself. 5173 is deliberately covered by
        // the ASP.NET Core http range rather than listed again.
        foreach (var port in new[] { 3000, 3010, 4173, 4200, 5000, 5173, 5300, 7000, 7300, 8000, 8080, 8090 })
            Assert.True(allowlist.IsAllowed(port), $"dev server port {port}");
        Assert.False(allowlist.IsAllowed(22));
        Assert.False(allowlist.IsAllowed(2999));
        Assert.False(allowlist.IsAllowed(9101));
    }

    [Fact]
    public void Options_DefaultsOfferTheKubernetesApiPort()
    {
        // 6443 was deliberately absent, on the reasoning that an agent reaching the API
        // server is an escalation. It is not one here: both pod specs set
        // AutomountServiceAccountToken = false and so does the service account, so a pod
        // carries no cluster credential and the request still needs the owner's approval.
        // Leaving it off meant a session asked to run kubectl against a cluster it has a
        // kubeconfig for was refused before anyone could decide.
        Assert.True(NetworkPortAllowlist.Parse(new NetworkPortOptions().RequestablePorts).IsAllowed(6443));
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
