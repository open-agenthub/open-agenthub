using System.Collections.Concurrent;

namespace AgentHub.Api.Library.ApiMcpGateway;

/// <summary>In-memory OpenAPI document cache with TTL.</summary>
public sealed class OpenApiSpecCache
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(15);

    private readonly HttpClient _http;
    private readonly TimeSpan _ttl;
    private readonly ConcurrentDictionary<string, CacheEntry> _entries = new(StringComparer.Ordinal);

    public OpenApiSpecCache(HttpClient http, TimeSpan? ttl = null)
    {
        _http = http;
        _ttl = ttl ?? DefaultTtl;
    }

    public async Task<string> GetAsync(string specUrl, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(specUrl);
        var key = specUrl.Trim();
        if (_entries.TryGetValue(key, out var hit) && hit.ExpiresAt > DateTimeOffset.UtcNow)
            return hit.Document;

        using var resp = await _http.GetAsync(key, ct);
        resp.EnsureSuccessStatusCode();
        var document = await resp.Content.ReadAsStringAsync(ct);
        _entries[key] = new CacheEntry(document, DateTimeOffset.UtcNow.Add(_ttl));
        return document;
    }

    public void Invalidate(string specUrl) => _entries.TryRemove(specUrl.Trim(), out _);

    private sealed record CacheEntry(string Document, DateTimeOffset ExpiresAt);
}
