namespace AgentHub.Api.Network;

/// <summary>
/// Configuration for runtime network port requests (bound from the "Network" section).
/// An agent may ask — via the built-in agenthub_network MCP server — to open additional
/// ports while the session runs; the session owner approves each request. Only ports on
/// the allowlist below can ever be requested; everything else is rejected without asking.
/// </summary>
public sealed class NetworkPortOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Comma-separated ports and "from-to" ranges an agent may request, e.g.
    /// "5432, 3306, 9000-9100". A single string (not an array) so that clearing the
    /// list in the chart reliably yields an empty allowlist instead of the default.
    /// The defaults cover common databases and a generic dev range.
    /// </summary>
    public string RequestablePorts { get; set; } = "5432, 3306, 6379, 27017, 9000-9100";
}

/// <summary>Parses the requestable-ports setting and answers allowlist checks.</summary>
public sealed class NetworkPortAllowlist
{
    private readonly IReadOnlyList<(int From, int To)> _ranges;

    private NetworkPortAllowlist(IReadOnlyList<(int From, int To)> ranges) => _ranges = ranges;

    /// <summary>
    /// Parses entries like "5432" and "9000-9100". Invalid or out-of-range tokens are
    /// dropped (fail closed: a typo never widens the list).
    /// </summary>
    public static NetworkPortAllowlist Parse(string? setting)
    {
        var ranges = new List<(int, int)>();
        foreach (var raw in (setting ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = raw.Split('-', StringSplitOptions.TrimEntries);
            if (parts.Length == 1 && TryPort(parts[0], out var single))
                ranges.Add((single, single));
            else if (parts.Length == 2 && TryPort(parts[0], out var from) && TryPort(parts[1], out var to) && from <= to)
                ranges.Add((from, to));
        }
        return new NetworkPortAllowlist(ranges);
    }

    private static bool TryPort(string text, out int port) =>
        int.TryParse(text, out port) && port is >= 1 and <= 65535;

    public bool IsAllowed(int port) => _ranges.Any(r => port >= r.From && port <= r.To);

    public bool IsEmpty => _ranges.Count == 0;
}
