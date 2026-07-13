using AgentHub.Api.Otel;
using Npgsql;

namespace AgentHub.Api.Persistence;

/// <summary>Aggregated token/cost usage for a single session (owner-scoped).</summary>
public sealed class SessionUsage
{
    public required string SessionId { get; init; }
    public required string Owner { get; init; }
    /// <summary>Session title (joined from the sessions table); may be null if the session was deleted.</summary>
    public string? Title { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long CacheReadTokens { get; init; }
    public long CacheCreationTokens { get; init; }
    public double CostUsd { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public long TotalTokens => InputTokens + OutputTokens + CacheReadTokens + CacheCreationTokens;
}

/// <summary>Owner-wide totals over an optional time window (filtered on updated_at).</summary>
public sealed class UsageSummary
{
    public required string Owner { get; init; }
    public int SessionCount { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long CacheReadTokens { get; init; }
    public long CacheCreationTokens { get; init; }
    public double CostUsd { get; init; }
    public DateTime? From { get; init; }
    public DateTime? To { get; init; }
    public long TotalTokens => InputTokens + OutputTokens + CacheReadTokens + CacheCreationTokens;
}

public interface IUsageStore
{
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>
    /// Adds one OTLP export's deltas to the session's aggregate row (created on first sight).
    /// The owner is resolved from the sessions table by <c>session.id</c>; the telemetry-supplied
    /// <c>user.id</c> is only a fallback. Returns false when no owner can be determined (row skipped).
    /// </summary>
    Task<bool> AddDeltaAsync(SessionUsageDelta delta, CancellationToken ct = default);

    Task<IReadOnlyList<SessionUsage>> ListByOwnerAsync(string owner, CancellationToken ct = default);
    Task<SessionUsage?> GetAsync(string owner, string sessionId, CancellationToken ct = default);
    Task<UsageSummary> SummaryAsync(string owner, DateTime? from, DateTime? to, CancellationToken ct = default);
}

/// <summary>
/// Persists per-session token/cost aggregates in Postgres. One row per session; each incoming
/// OTLP export adds its (delta) values. Owner-level views are derived by grouping on owner.
/// Independent NpgsqlDataSource, mirroring <see cref="ApiTokenStore"/>.
/// </summary>
public sealed class PostgresUsageStore : IUsageStore
{
    private readonly NpgsqlDataSource _db;

    public PostgresUsageStore(IConfiguration cfg)
    {
        var cs = cfg.GetConnectionString("Postgres")
                 ?? throw new InvalidOperationException("ConnectionStrings:Postgres is missing.");
        _db = NpgsqlDataSource.Create(cs);
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        const string ddl = """
            CREATE TABLE IF NOT EXISTS session_usage (
                session_id            TEXT PRIMARY KEY,
                owner                 TEXT NOT NULL,
                input_tokens          BIGINT NOT NULL DEFAULT 0,
                output_tokens         BIGINT NOT NULL DEFAULT 0,
                cache_read_tokens     BIGINT NOT NULL DEFAULT 0,
                cache_creation_tokens BIGINT NOT NULL DEFAULT 0,
                cost_usd              DOUBLE PRECISION NOT NULL DEFAULT 0,
                created_at            TIMESTAMPTZ NOT NULL DEFAULT now(),
                updated_at            TIMESTAMPTZ NOT NULL DEFAULT now()
            );
            CREATE INDEX IF NOT EXISTS idx_session_usage_owner ON session_usage(owner);
            CREATE INDEX IF NOT EXISTS idx_session_usage_updated ON session_usage(updated_at);
            """;
        await using var cmd = _db.CreateCommand(ddl);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> AddDeltaAsync(SessionUsageDelta delta, CancellationToken ct = default)
    {
        // Resolve the authoritative owner from the sessions registry; fall back to the
        // telemetry user.id only if the session row is unknown (e.g. already deleted).
        var owner = await ResolveOwnerAsync(delta.SessionId, ct) ?? delta.UserId;
        if (string.IsNullOrEmpty(owner)) return false;

        const string sql = """
            INSERT INTO session_usage
                (session_id, owner, input_tokens, output_tokens, cache_read_tokens, cache_creation_tokens, cost_usd, updated_at)
            VALUES (@sid, @owner, @in, @out, @cr, @cc, @cost, now())
            ON CONFLICT (session_id) DO UPDATE SET
                owner                 = EXCLUDED.owner,
                input_tokens          = session_usage.input_tokens          + EXCLUDED.input_tokens,
                output_tokens         = session_usage.output_tokens         + EXCLUDED.output_tokens,
                cache_read_tokens     = session_usage.cache_read_tokens     + EXCLUDED.cache_read_tokens,
                cache_creation_tokens = session_usage.cache_creation_tokens + EXCLUDED.cache_creation_tokens,
                cost_usd              = session_usage.cost_usd              + EXCLUDED.cost_usd,
                updated_at            = now();
            """;
        await using var cmd = _db.CreateCommand(sql);
        cmd.Parameters.AddWithValue("sid", delta.SessionId);
        cmd.Parameters.AddWithValue("owner", owner);
        cmd.Parameters.AddWithValue("in", delta.InputTokens);
        cmd.Parameters.AddWithValue("out", delta.OutputTokens);
        cmd.Parameters.AddWithValue("cr", delta.CacheReadTokens);
        cmd.Parameters.AddWithValue("cc", delta.CacheCreationTokens);
        cmd.Parameters.AddWithValue("cost", delta.CostUsd);
        await cmd.ExecuteNonQueryAsync(ct);
        return true;
    }

    private async Task<string?> ResolveOwnerAsync(string sessionId, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand("SELECT owner FROM sessions WHERE id = @id");
        cmd.Parameters.AddWithValue("id", sessionId);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    private const string SelectBase = """
        SELECT u.session_id, u.owner, s.title,
               u.input_tokens, u.output_tokens, u.cache_read_tokens, u.cache_creation_tokens,
               u.cost_usd, u.created_at, u.updated_at
        FROM session_usage u
        LEFT JOIN sessions s ON s.id = u.session_id
        """;

    public async Task<IReadOnlyList<SessionUsage>> ListByOwnerAsync(string owner, CancellationToken ct = default)
    {
        var list = new List<SessionUsage>();
        await using var cmd = _db.CreateCommand($"{SelectBase} WHERE u.owner = @owner ORDER BY u.updated_at DESC");
        cmd.Parameters.AddWithValue("owner", owner);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(Map(r));
        return list;
    }

    public async Task<SessionUsage?> GetAsync(string owner, string sessionId, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand($"{SelectBase} WHERE u.owner = @owner AND u.session_id = @sid");
        cmd.Parameters.AddWithValue("owner", owner);
        cmd.Parameters.AddWithValue("sid", sessionId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Map(r) : null;
    }

    public async Task<UsageSummary> SummaryAsync(string owner, DateTime? from, DateTime? to, CancellationToken ct = default)
    {
        // Time window filters on updated_at (the only timestamp an aggregate row carries).
        var where = "WHERE owner = @owner";
        if (from is not null) where += " AND updated_at >= @from";
        if (to is not null) where += " AND updated_at <= @to";
        var sql = $"""
            SELECT COUNT(*),
                   COALESCE(SUM(input_tokens), 0),
                   COALESCE(SUM(output_tokens), 0),
                   COALESCE(SUM(cache_read_tokens), 0),
                   COALESCE(SUM(cache_creation_tokens), 0),
                   COALESCE(SUM(cost_usd), 0)
            FROM session_usage {where}
            """;
        await using var cmd = _db.CreateCommand(sql);
        cmd.Parameters.AddWithValue("owner", owner);
        if (from is not null) cmd.Parameters.AddWithValue("from", from.Value);
        if (to is not null) cmd.Parameters.AddWithValue("to", to.Value);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return new UsageSummary
        {
            Owner = owner,
            SessionCount = (int)r.GetInt64(0),
            InputTokens = r.GetInt64(1),
            OutputTokens = r.GetInt64(2),
            CacheReadTokens = r.GetInt64(3),
            CacheCreationTokens = r.GetInt64(4),
            CostUsd = r.GetDouble(5),
            From = from, To = to
        };
    }

    private static SessionUsage Map(NpgsqlDataReader r) => new()
    {
        SessionId = r.GetString(0),
        Owner = r.GetString(1),
        Title = r.IsDBNull(2) ? null : r.GetString(2),
        InputTokens = r.GetInt64(3),
        OutputTokens = r.GetInt64(4),
        CacheReadTokens = r.GetInt64(5),
        CacheCreationTokens = r.GetInt64(6),
        CostUsd = r.GetDouble(7),
        CreatedAt = r.GetDateTime(8),
        UpdatedAt = r.GetDateTime(9)
    };
}
