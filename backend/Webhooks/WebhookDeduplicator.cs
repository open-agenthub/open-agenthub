namespace AgentHub.Api.Webhooks;

/// <summary>
/// In-memory delivery dedup: providers retry webhooks (and users double-fire hooks),
/// so the same MR/PR + action must not start two sessions within a short window.
/// Per-replica only — good enough for v1; a DB constraint can replace it later.
/// </summary>
public sealed class WebhookDeduplicator
{
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(5);

    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _seen = new();
    private readonly TimeSpan _window;
    private readonly TimeProvider _time;

    public WebhookDeduplicator(TimeProvider? time = null, TimeSpan? window = null)
    {
        _time = time ?? TimeProvider.System;
        _window = window ?? DefaultWindow;
    }

    /// <summary>True exactly once per key and window; marks the key as seen
    /// BEFORE the session is created so a concurrent retry never doubles up.</summary>
    public bool TryBegin(string key)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            // Prune on write: traffic is low-volume, so a full sweep stays cheap.
            foreach (var expired in _seen.Where(p => now - p.Value >= _window).Select(p => p.Key).ToList())
                _seen.Remove(expired);
            if (_seen.ContainsKey(key)) return false;
            _seen[key] = now;
            return true;
        }
    }
}
