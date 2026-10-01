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
    /// <para>
    /// Being on this list is permission to <em>ask</em>, not access: every request still
    /// goes to the session owner. The defaults therefore cover what an agent plausibly
    /// needs — backing services and the dev server a framework starts on its own — because
    /// a port missing from the list is rejected without the owner ever seeing the question,
    /// which reads to the agent as "the platform cannot do this" rather than "ask again".
    /// </para>
    /// <list type="bullet">
    ///   <item>3000-3010 — React, Next.js, Nuxt, and the usual Node servers</item>
    ///   <item>3306 MariaDB/MySQL, 5432 PostgreSQL, 6379 Redis, 27017 MongoDB</item>
    ///   <item>3900 and 9000-9100 — self-hosted object storage (Garage, MinIO) and php-fpm</item>
    ///   <item>4173 Vite preview, 4200-4210 Angular</item>
    ///   <item>5000-5300 and 7000-7300 — the http/https pair an ASP.NET Core template picks,
    ///         which also covers Flask and the Vite dev server on 5173</item>
    ///   <item>8000-8010 Django, FastAPI, Laravel and PHP's built-in server; 8080-8090 generic</item>
    ///   <item>6443 — the Kubernetes API. Safe to offer because a session pod mounts no service
    ///         account token (<c>AutomountServiceAccountToken = false</c> in both pod specs and
    ///         on the service account itself), so reaching the API server grants nothing the
    ///         agent does not already have credentials for.</item>
    /// </list>
    /// </summary>
    public string RequestablePorts { get; set; } =
        "3000-3010, 3306, 3900, 4173, 4200-4210, 5000-5300, 5432, 6379, 6443, " +
        "7000-7300, 8000-8010, 8080-8090, 9000-9100, 27017";
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
