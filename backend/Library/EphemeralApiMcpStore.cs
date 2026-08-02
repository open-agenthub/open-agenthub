using System.Collections.Concurrent;
using Npgsql;

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
    Task InitializeAsync(CancellationToken ct = default);
    Task RegisterAsync(EphemeralApiMcpEntry entry, CancellationToken ct = default);
    Task<EphemeralApiMcpEntry?> GetAsync(string sessionId, string name, CancellationToken ct = default);
    Task<IReadOnlyList<EphemeralApiMcpEntry>> ListBySessionAsync(string sessionId, CancellationToken ct = default);
    Task DeleteBySessionAsync(string sessionId, CancellationToken ct = default);
}

/// <summary>In-memory ephemeral API MCP registry keyed by sessionId + name (unit tests).</summary>
public sealed class InMemoryEphemeralApiMcpStore : IEphemeralApiMcpStore
{
    private readonly ConcurrentDictionary<string, EphemeralApiMcpEntry> _entries = new(StringComparer.Ordinal);

    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task RegisterAsync(EphemeralApiMcpEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.SessionId);
        LibraryValidation.ValidateMcpServerName(entry.Name);
        LibraryValidation.ValidateMcpServerConfig(entry.ConfigJson, "api");
        _entries[Key(entry.SessionId, entry.Name)] = entry;
        return Task.CompletedTask;
    }

    public Task<EphemeralApiMcpEntry?> GetAsync(string sessionId, string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(name))
            return Task.FromResult<EphemeralApiMcpEntry?>(null);
        return Task.FromResult(
            _entries.TryGetValue(Key(sessionId, name), out var entry) ? entry : null);
    }

    public Task<IReadOnlyList<EphemeralApiMcpEntry>> ListBySessionAsync(
        string sessionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return Task.FromResult<IReadOnlyList<EphemeralApiMcpEntry>>([]);
        var prefix = sessionId.Trim() + "\n";
        IReadOnlyList<EphemeralApiMcpEntry> list = _entries
            .Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal))
            .Select(kv => kv.Value)
            .OrderBy(e => e.Name, StringComparer.Ordinal)
            .ToList();
        return Task.FromResult(list);
    }

    public Task DeleteBySessionAsync(string sessionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return Task.CompletedTask;
        var prefix = sessionId.Trim() + "\n";
        foreach (var key in _entries.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            _entries.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    private static string Key(string sessionId, string name) =>
        $"{sessionId.Trim()}\n{name.Trim()}";
}

/// <summary>Postgres persistence for session-scoped ephemeral API MCP registrations.</summary>
public sealed class EphemeralApiMcpStore : IEphemeralApiMcpStore
{
    private readonly NpgsqlDataSource _db;
    private readonly IMcpSecretProtector _secrets;

    public EphemeralApiMcpStore(IConfiguration cfg, IMcpSecretProtector secrets)
    {
        var cs = cfg.GetConnectionString("Postgres")
                 ?? throw new InvalidOperationException("ConnectionStrings:Postgres is missing.");
        _db = NpgsqlDataSource.Create(cs);
        _secrets = secrets;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        const string ddl = """
            CREATE TABLE IF NOT EXISTS ephemeral_api_mcp (
                session_id  TEXT NOT NULL,
                name        TEXT NOT NULL,
                owner       TEXT NOT NULL,
                config_json TEXT NOT NULL,
                secret_json TEXT,
                created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
                PRIMARY KEY (session_id, name)
            );
            CREATE INDEX IF NOT EXISTS idx_ephemeral_api_mcp_session
                ON ephemeral_api_mcp(session_id);
            """;
        await using var cmd = _db.CreateCommand(ddl);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RegisterAsync(EphemeralApiMcpEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.SessionId);
        var name = LibraryValidation.ValidateMcpServerName(entry.Name);
        var config = LibraryValidation.ValidateMcpServerConfig(entry.ConfigJson, "api");
        var sessionId = entry.SessionId.Trim();
        var secret = string.IsNullOrEmpty(entry.SecretJson) ? null : entry.SecretJson;

        const string sql = """
            INSERT INTO ephemeral_api_mcp (session_id, name, owner, config_json, secret_json)
            VALUES (@session_id, @name, @owner, @config_json, @secret_json)
            ON CONFLICT (session_id, name) DO UPDATE
            SET owner = EXCLUDED.owner,
                config_json = EXCLUDED.config_json,
                secret_json = EXCLUDED.secret_json,
                created_at = now()
            """;
        await using var cmd = _db.CreateCommand(sql);
        cmd.Parameters.AddWithValue("session_id", sessionId);
        cmd.Parameters.AddWithValue("name", name);
        cmd.Parameters.AddWithValue("owner", entry.Owner);
        cmd.Parameters.AddWithValue("config_json", config);
        cmd.Parameters.AddWithValue("secret_json", (object?)_secrets.Protect(secret) ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<EphemeralApiMcpEntry?> GetAsync(
        string sessionId, string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(name))
            return null;

        const string sql = """
            SELECT session_id, name, owner, config_json, secret_json
            FROM ephemeral_api_mcp
            WHERE session_id = @session_id AND name = @name
            """;
        await using var cmd = _db.CreateCommand(sql);
        cmd.Parameters.AddWithValue("session_id", sessionId.Trim());
        cmd.Parameters.AddWithValue("name", name.Trim());
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        return ReadEntry(reader);
    }

    public async Task<IReadOnlyList<EphemeralApiMcpEntry>> ListBySessionAsync(
        string sessionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return [];

        const string sql = """
            SELECT session_id, name, owner, config_json, secret_json
            FROM ephemeral_api_mcp
            WHERE session_id = @session_id
            ORDER BY name
            """;
        await using var cmd = _db.CreateCommand(sql);
        cmd.Parameters.AddWithValue("session_id", sessionId.Trim());
        var list = new List<EphemeralApiMcpEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(ReadEntry(reader));
        return list;
    }

    public async Task DeleteBySessionAsync(string sessionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        await using var cmd = _db.CreateCommand(
            "DELETE FROM ephemeral_api_mcp WHERE session_id = @session_id");
        cmd.Parameters.AddWithValue("session_id", sessionId.Trim());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private EphemeralApiMcpEntry ReadEntry(NpgsqlDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            _secrets.Unprotect(reader.IsDBNull(4) ? null : reader.GetString(4)));
}
