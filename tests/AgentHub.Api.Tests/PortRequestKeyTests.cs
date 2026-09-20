using AgentHub.Api.Network;
using Xunit;

namespace AgentHub.Api.Tests;

public class PortRequestKeyTests
{
    [Theory]
    [InlineData(PortDirection.Egress, 5432, "TCP", "NetworkPort(egress 5432/TCP)")]
    [InlineData(PortDirection.Egress, 53, "udp", "NetworkPort(egress 53/UDP)")]
    [InlineData(PortDirection.BrowserToAgent, 3000, "TCP", "NetworkPort(browser_to_agent 3000/TCP)")]
    public void Format_ProducesTheCanonicalKey(PortDirection direction, int port, string protocol, string expected)
    {
        Assert.Equal(expected, PortRequestKey.Format(direction, port, protocol));
    }

    [Theory]
    [InlineData("NetworkPort(egress 5432/TCP)", PortDirection.Egress, 5432, "TCP")]
    [InlineData("NetworkPort(browser_to_agent 3000/TCP)", PortDirection.BrowserToAgent, 3000, "TCP")]
    [InlineData("NetworkPort(egress 53/UDP)", PortDirection.Egress, 53, "UDP")]
    public void TryParse_RoundTrips(string key, PortDirection direction, int port, string protocol)
    {
        Assert.True(PortRequestKey.TryParse(key, out var parsedDirection, out var parsedPort, out var parsedProtocol));
        Assert.Equal(direction, parsedDirection);
        Assert.Equal(port, parsedPort);
        Assert.Equal(protocol, parsedProtocol);
        Assert.Equal(key, PortRequestKey.Format(parsedDirection, parsedPort, parsedProtocol));
    }

    [Theory]
    [InlineData("Bash")]
    [InlineData("NetworkPort(sideways 80/TCP)")]
    [InlineData("NetworkPort(egress 80/ICMP)")]
    [InlineData("NetworkPort(egress 0/TCP)")]
    [InlineData("NetworkPort(egress 99999/TCP)")]
    [InlineData("NetworkPort(egress 80/TCP) extra")]
    [InlineData(null)]
    public void TryParse_RejectsNonPortKeys(string? key)
    {
        Assert.False(PortRequestKey.TryParse(key, out _, out _, out _));
    }

    [Theory]
    [InlineData("egress", true, PortDirection.Egress)]
    [InlineData("browser_to_agent", true, PortDirection.BrowserToAgent)]
    [InlineData("ingress", false, PortDirection.Egress)]
    [InlineData(null, false, PortDirection.Egress)]
    public void TryParseDirection_AcceptsOnlyTheWireNames(string? wire, bool expected, PortDirection direction)
    {
        Assert.Equal(expected, PortRequestKey.TryParseDirection(wire, out var parsed));
        if (expected) Assert.Equal(direction, parsed);
    }
}
