using System.Collections.Concurrent;
using Npgsql;

namespace AgentHub.Api.Persistence;

/// <summary>
/// An agent's request for a secret it does not have (docs/session-secrets.md): a name, the wanted
/// delivery and what it is for — never a value, which the owner types in the web app.
/// </summary>
public sealed record SecretRequest
{
    public const string Pending = "pending";
    public const string Provided = "provided";
    public const string Refused = "refused";
    public const string Expired = "expired";

    public required string Id { get; init; }
    public required string SessionId { get; init; }
    public required string Owner { get; init; }
    public required string Name { get; init; }
    public required string Delivery { get; init; }
    public string? Purpose { get; init; }
    public string Status { get; init; } = Pending;
    /// <summary>The stored secret the owner answered with; set once provided.</summary>
    public string? SecretId { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime? ResolvedAt { get; init; }
}

public interface ISecretRequestStore
{
    Task InitializeAsync(CancellationToken ct = default);
    Task CreateAsync(SecretRequest request, CancellationToken ct = default);
    Task<SecretRequest?> GetAsync(string id, CancellationToken ct = default);
    /// <summary>The owner's open requests, newest first; one session's when <paramref name="sessionId"/> is set.</summary>
    Task<IReadOnlyList<SecretRequest>> ListPendingAsync(string owner, string? sessionId = null, CancellationToken ct = default);
    /// <summary>
    /// Closes a pending request. False when it was no longer pending: the owner's answer and the
    /// tool giving up can race, and the first one wins on every replica.
    /// </summary>
    Task<bool> ResolveAsync(string id, string status, string? secretId, CancellationToken ct = default);
    /// <summary>Drops a session's requests with the session.</summary>
    Task DeleteBySessionAsync(string sessionId, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>
/// Postgres, not memory: the tool polls through one replica while the owner answers through
/// another, as with permission requests.
/// </summary>
public sealed class PostgresSecretRequestStore(IConfiguration cfg) : ISecretRequestStore
{
    private readonly NpgsqlDataSource _db = NpgsqlDataSource.Create(
        cfg.GetConnectionString("Postgres") ?? throw new InvalidOperationException("ConnectionStrings:Postgres is missing."));

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        const string ddl = """
            CREATE TABLE IF NOT EXISTS secret_requests (
                id          TEXT PRIMARY KEY,
                session_id  TEXT NOT NULL,
                owner       TEXT NOT NULL,
                name        TEXT NOT NULL,
                delivery    TEXT NOT NULL,
                purpose     TEXT,
                status      TEXT NOT NULL DEFAULT 'pending',
                secret_id   TEXT,
                created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
                resolved_at TIMESTAMPTZ
            );
            CREATE INDEX IF NOT EXISTS idx_secret_requests_owner ON secret_requests(owner, status);
            CREATE INDEX IF NOT EXISTS idx_secret_requests_session ON secret_requests(session_id);
            """;
        await using var cmd = _db.CreateCommand(ddl);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task CreateAsync(SecretRequest r, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand("""
            INSERT INTO secret_requests (id, session_id, owner, name, delivery, purpose, status, created_at)
            VALUES (@id, @session, @owner, @name, @delivery, @purpose, @status, @created)
            """);
        cmd.Parameters.AddWithValue("id", r.Id);
        cmd.Parameters.AddWithValue("session", r.SessionId);
        cmd.Parameters.AddWithValue("owner", r.Owner);
        cmd.Parameters.AddWithValue("name", r.Name);
        cmd.Parameters.AddWithValue("delivery", r.Delivery);
        cmd.Parameters.AddWithValue("purpose", (object?)r.Purpose ?? DBNull.Value);
        cmd.Parameters.AddWithValue("status", r.Status);
        cmd.Parameters.AddWithValue("created", r.CreatedAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<SecretRequest?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand($"{Select} WHERE id = @id");
        cmd.Parameters.AddWithValue("id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Map(reader) : null;
    }

    public async Task<IReadOnlyList<SecretRequest>> ListPendingAsync(string owner, string? sessionId = null, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand($"""
            {Select} WHERE owner = @owner AND status = 'pending' AND (@session::text IS NULL OR session_id = @session)
            ORDER BY created_at DESC LIMIT 100
            """);
        cmd.Parameters.AddWithValue("owner", owner);
        cmd.Parameters.AddWithValue("session", (object?)sessionId ?? DBNull.Value);
        var list = new List<SecretRequest>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) list.Add(Map(reader));
        return list;
    }

    public async Task<bool> ResolveAsync(string id, string status, string? secretId, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand("""
            UPDATE secret_requests SET status = @status, secret_id = @secret, resolved_at = now()
            WHERE id = @id AND status = 'pending'
            """);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("status", status);
        cmd.Parameters.AddWithValue("secret", (object?)secretId ?? DBNull.Value);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task DeleteBySessionAsync(string sessionId, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand("DELETE FROM secret_requests WHERE session_id = @session");
        cmd.Parameters.AddWithValue("session", sessionId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private const string Select =
        "SELECT id, session_id, owner, name, delivery, purpose, status, secret_id, created_at, resolved_at FROM secret_requests";

    private static SecretRequest Map(NpgsqlDataReader r) => new()
    {
        Id = r.GetString(0),
        SessionId = r.GetString(1),
        Owner = r.GetString(2),
        Name = r.GetString(3),
        Delivery = r.GetString(4),
        Purpose = r.IsDBNull(5) ? null : r.GetString(5),
        Status = r.GetString(6),
        SecretId = r.IsDBNull(7) ? null : r.GetString(7),
        CreatedAt = r.GetDateTime(8),
        ResolvedAt = r.IsDBNull(9) ? null : r.GetDateTime(9)
    };
}

/// <summary>For tests and a backend without Postgres.</summary>
public sealed class InMemorySecretRequestStore : ISecretRequestStore
{
    private readonly ConcurrentDictionary<string, SecretRequest> _items = new();

    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task CreateAsync(SecretRequest request, CancellationToken ct = default)
    {
        _items[request.Id] = request;
        return Task.CompletedTask;
    }

    public Task<SecretRequest?> GetAsync(string id, CancellationToken ct = default) =>
        Task.FromResult(_items.GetValueOrDefault(id));

    public Task<IReadOnlyList<SecretRequest>> ListPendingAsync(string owner, string? sessionId = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SecretRequest>>(_items.Values
            .Where(r => r.Owner == owner && r.Status == SecretRequest.Pending && (sessionId is null || r.SessionId == sessionId))
            .OrderByDescending(r => r.CreatedAt).ToList());

    public Task<bool> ResolveAsync(string id, string status, string? secretId, CancellationToken ct = default)
    {
        lock (_items)
        {
            if (!_items.TryGetValue(id, out var current) || current.Status != SecretRequest.Pending)
                return Task.FromResult(false);
            _items[id] = current with { Status = status, SecretId = secretId, ResolvedAt = DateTime.UtcNow };
            return Task.FromResult(true);
        }
    }

    public Task DeleteBySessionAsync(string sessionId, CancellationToken ct = default)
    {
        foreach (var id in _items.Values.Where(r => r.SessionId == sessionId).Select(r => r.Id).ToList())
            _items.TryRemove(id, out _);
        return Task.CompletedTask;
    }
}
