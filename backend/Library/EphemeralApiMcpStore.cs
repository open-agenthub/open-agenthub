using System.Collections.Concurrent;

namespace AgentHub.Api.Library;

/// <summary>Session-scoped API MCP registration (not a catalog row).</summary>
public sealed record EphemeralApiMcpEntry(
    string SessionId,
    string Name,
    string Owner,
    string ConfigJson,
    string? SecretJson);

public interface IEphemeralApiMcpStore
{
    void Register(EphemeralApiMcpEntry entry);
    EphemeralApiMcpEntry? Get(string sessionId, string name);
    IReadOnlyList<EphemeralApiMcpEntry> ListBySession(string sessionId);
    void DeleteBySession(string sessionId);
}

/// <summary>In-memory ephemeral API MCP registry keyed by sessionId + name (v1).</summary>
public sealed class EphemeralApiMcpStore : IEphemeralApiMcpStore
{
    private readonly ConcurrentDictionary<string, EphemeralApiMcpEntry> _entries = new(StringComparer.Ordinal);

    public void Register(EphemeralApiMcpEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.SessionId);
        LibraryValidation.ValidateMcpServerName(entry.Name);
        LibraryValidation.ValidateMcpServerConfig(entry.ConfigJson, "api");
        _entries[Key(entry.SessionId, entry.Name)] = entry;
    }

    public EphemeralApiMcpEntry? Get(string sessionId, string name)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(name))
            return null;
        return _entries.TryGetValue(Key(sessionId, name), out var entry) ? entry : null;
    }

    public IReadOnlyList<EphemeralApiMcpEntry> ListBySession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return [];
        var prefix = sessionId.Trim() + "\n";
        return _entries
            .Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal))
            .Select(kv => kv.Value)
            .OrderBy(e => e.Name, StringComparer.Ordinal)
            .ToList();
    }

    public void DeleteBySession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return;
        var prefix = sessionId.Trim() + "\n";
        foreach (var key in _entries.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            _entries.TryRemove(key, out _);
    }

    private static string Key(string sessionId, string name) =>
        $"{sessionId.Trim()}\n{name.Trim()}";
}
