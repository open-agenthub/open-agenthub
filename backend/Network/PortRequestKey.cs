using System.Text.RegularExpressions;

namespace AgentHub.Api.Network;

public enum PortDirection
{
    /// <summary>The agent pod may open outbound connections to the port (anywhere).</summary>
    Egress,
    /// <summary>The session's browser pod may reach the agent pod on the port
    /// (e.g. a dev server the agent runs, beyond the default preview ports).</summary>
    BrowserToAgent
}

/// <summary>
/// Canonical tool key for a port request, e.g. "NetworkPort(egress 5432/TCP)".
/// Port requests ride on the existing tool-permission channel (PermissionStore,
/// messenger prompts, in-app approval); this key is what gets stored as the request's
/// tool name, so decisions, "don't ask again" rules and the UI all stay port-specific.
/// </summary>
public static partial class PortRequestKey
{
    public const string EgressWire = "egress";
    public const string BrowserToAgentWire = "browser_to_agent";

    [GeneratedRegex(@"^NetworkPort\((egress|browser_to_agent) (\d{1,5})/(TCP|UDP)\)$")]
    private static partial Regex KeyPattern();

    public static string Format(PortDirection direction, int port, string protocol) =>
        $"NetworkPort({WireName(direction)} {port}/{protocol.ToUpperInvariant()})";

    public static string WireName(PortDirection direction) => direction switch
    {
        PortDirection.Egress => EgressWire,
        PortDirection.BrowserToAgent => BrowserToAgentWire,
        _ => throw new ArgumentOutOfRangeException(nameof(direction))
    };

    public static bool TryParseDirection(string? wire, out PortDirection direction)
    {
        direction = PortDirection.Egress;
        switch (wire)
        {
            case EgressWire: return true;
            case BrowserToAgentWire: direction = PortDirection.BrowserToAgent; return true;
            default: return false;
        }
    }

    public static bool TryParse(string? tool, out PortDirection direction, out int port, out string protocol)
    {
        direction = PortDirection.Egress;
        port = 0;
        protocol = "TCP";
        if (tool is null) return false;
        var match = KeyPattern().Match(tool);
        if (!match.Success) return false;
        if (!TryParseDirection(match.Groups[1].Value, out direction)) return false;
        if (!int.TryParse(match.Groups[2].Value, out port) || port is < 1 or > 65535) return false;
        protocol = match.Groups[3].Value;
        return true;
    }
}
