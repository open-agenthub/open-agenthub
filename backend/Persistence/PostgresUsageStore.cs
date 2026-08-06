using AgentHub.Api.Otel;
using AgentHub.Api.Usage;
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
    /// <summary>Cost as reported by Claude Code — 0 for subscription (OAuth) sessions.</summary>
    public double CostUsd { get; init; }
    /// <summary>What the tokens would have cost on the API, from the static price table.</summary>
    public double EstimatedCostUsd { get; init; }
    /// <summary>Session auth mode snapshot ("Subscription" | "ApiKey" | "Auto"); null for old rows.</summary>
    public string? AuthMode { get; init; }
    /// <summary>Total CPU time (seconds) consumed by the session's pods.</summary>
    public double CpuSeconds { get; init; }
    /// <summary>Memory footprint (bytes) at the last pod snapshot.</summary>
    public long MemoryBytes { get; init; }
    /// <summary>Highest memory footprint (bytes) seen across all snapshots.</summary>
    public long PeakMemoryBytes { get; init; }
    /// <summary>Total network bytes received/sent by the session's pods.</summary>
    public long RxBytes { get; init; }
    public long TxBytes { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public long TotalTokens => InputTokens + OutputTokens + CacheReadTokens + CacheCreationTokens;
    /// <summary>True when this session actually billed against an API key (real spend).</summary>
    public bool ApiBilled => AuthMode == "ApiKey" || CostUsd > 0;
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
    /// <summary>Real API spend (sum of reported costs; subscription sessions report 0).</summary>
    public double CostUsd { get; init; }
    /// <summary>Estimated API-equivalent cost of ALL tokens (api + subscription sessions).</summary>
    public double EstimatedCostUsd { get; init; }
    /// <summary>"Would have cost" — estimated cost of the subscription-covered sessions only.</summary>
    public double SubscriptionEstimatedCostUsd { get; init; }
    /// <summary>Pod resource totals (see <see cref="SessionUsage"/>).</summary>
    public double CpuSeconds { get; init; }
    public long RxBytes { get; init; }
    public long TxBytes { get; init; }
    public DateTime? From { get; init; }
    public DateTime? To { get; init; }
    public long TotalTokens => InputTokens + OutputTokens + CacheReadTokens + CacheCreationTokens;
    /// <summary>Alias for the real API spend, so the intent is explicit on the wire.</summary>
    public double ApiCostUsd => CostUsd;
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

    /// <summary>
    /// Records a pod resource snapshot (cumulative counters + memory gauge) on the session's
    /// aggregate row. The owner comes from the already-authorized session record, so no lookup
    /// happens here. Counter resets (new pod after a resume) are handled as full deltas.
    /// </summary>
    Task AddResourceSampleAsync(string sessionId, string owner, SessionResourceSample sample, CancellationToken ct = default);

    Task<IReadOnlyList<SessionUsage>> ListByOwnerAsync(string owner, CancellationToken ct = default);
    Task<SessionUsage?> GetAsync(string owner, string sessionId, CancellationToken ct = default);
    Task<UsageSummary> SummaryAsync(string owner, DateTime? from, DateTime? to, CancellationToken ct = default);

    /// <summary>Real API spend (reported cost) of the owner in the current calendar month (UTC).</summary>
    Task<double> MonthToDateApiCostAsync(string owner, CancellationToken ct = default);
}

/// <summary>
/// Persists per-session token/cost aggregates in Postgres. One row per session; each incoming
/// OTLP export adds its (delta) values. Owner-level views are derived by grouping on owner.
/// A per-owner monthly rollup (usage_monthly) attributes cost deltas to the month they arrive
/// in, so monthly usage limits stay exact even when a session spans a month boundary.
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
            -- Real-vs-estimated split (subscription sessions report cost 0; see ClaudePricing).
            ALTER TABLE session_usage ADD COLUMN IF NOT EXISTS estimated_cost_usd DOUBLE PRECISION NOT NULL DEFAULT 0;
            ALTER TABLE session_usage ADD COLUMN IF NOT EXISTS auth_mode TEXT;
            -- Pod resource consumption (fed by POST /internal/sessions/{id}/resources).
            -- cpu/rx/tx are session totals; the last_* columns hold the previous pod counter
            -- snapshot so each report only adds its positive delta (resume = counter reset).
            ALTER TABLE session_usage ADD COLUMN IF NOT EXISTS cpu_seconds       DOUBLE PRECISION NOT NULL DEFAULT 0;
            ALTER TABLE session_usage ADD COLUMN IF NOT EXISTS memory_bytes      BIGINT NOT NULL DEFAULT 0;
            ALTER TABLE session_usage ADD COLUMN IF NOT EXISTS peak_memory_bytes BIGINT NOT NULL DEFAULT 0;
            ALTER TABLE session_usage ADD COLUMN IF NOT EXISTS rx_bytes          BIGINT NOT NULL DEFAULT 0;
            ALTER TABLE session_usage ADD COLUMN IF NOT EXISTS tx_bytes          BIGINT NOT NULL DEFAULT 0;
            ALTER TABLE session_usage ADD COLUMN IF NOT EXISTS last_cpu_seconds  DOUBLE PRECISION NOT NULL DEFAULT 0;
            ALTER TABLE session_usage ADD COLUMN IF NOT EXISTS last_rx_bytes     BIGINT NOT NULL DEFAULT 0;
            ALTER TABLE session_usage ADD COLUMN IF NOT EXISTS last_tx_bytes     BIGINT NOT NULL DEFAULT 0;
            CREATE INDEX IF NOT EXISTS idx_session_usage_owner ON session_usage(owner);
            CREATE INDEX IF NOT EXISTS idx_session_usage_updated ON session_usage(updated_at);
            -- Monthly rollup: deltas are attributed to the month they arrive in, so limits are
            -- exact per calendar month (session aggregates only carry updated_at).
            CREATE TABLE IF NOT EXISTS usage_monthly (
                owner              TEXT NOT NULL,
                month              DATE NOT NULL,
                cost_usd           DOUBLE PRECISION NOT NULL DEFAULT 0,
                estimated_cost_usd DOUBLE PRECISION NOT NULL DEFAULT 0,
                updated_at         TIMESTAMPTZ NOT NULL DEFAULT now(),
                PRIMARY KEY (owner, month)
            );
            """;
        await using var cmd = _db.CreateCommand(ddl);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> AddDeltaAsync(SessionUsageDelta delta, CancellationToken ct = default)
    {
        // Resolve the authoritative owner (and the auth-mode snapshot) from the sessions
        // registry; fall back to the telemetry user.id only if the session row is unknown.
        var (owner, authMode) = await ResolveSessionAsync(delta.SessionId, ct);
        owner ??= delta.UserId;
        if (string.IsNullOrEmpty(owner)) return false;

        var estimated = ClaudePricing.EstimateUsd(delta);

        const string sql = """
            INSERT INTO session_usage
                (session_id, owner, input_tokens, output_tokens, cache_read_tokens, cache_creation_tokens,
                 cost_usd, estimated_cost_usd, auth_mode, updated_at)
            VALUES (@sid, @owner, @in, @out, @cr, @cc, @cost, @est, @auth, now())
            ON CONFLICT (session_id) DO UPDATE SET
                owner                 = EXCLUDED.owner,
                input_tokens          = session_usage.input_tokens          + EXCLUDED.input_tokens,
                output_tokens         = session_usage.output_tokens         + EXCLUDED.output_tokens,
                cache_read_tokens     = session_usage.cache_read_tokens     + EXCLUDED.cache_read_tokens,
                cache_creation_tokens = session_usage.cache_creation_tokens + EXCLUDED.cache_creation_tokens,
                cost_usd              = session_usage.cost_usd              + EXCLUDED.cost_usd,
                estimated_cost_usd    = session_usage.estimated_cost_usd    + EXCLUDED.estimated_cost_usd,
                auth_mode             = COALESCE(EXCLUDED.auth_mode, session_usage.auth_mode),
                updated_at            = now();

            INSERT INTO usage_monthly (owner, month, cost_usd, estimated_cost_usd, updated_at)
            VALUES (@owner, date_trunc('month', now())::date, @cost, @est, now())
            ON CONFLICT (owner, month) DO UPDATE SET
                cost_usd           = usage_monthly.cost_usd           + EXCLUDED.cost_usd,
                estimated_cost_usd = usage_monthly.estimated_cost_usd + EXCLUDED.estimated_cost_usd,
                updated_at         = now();
            """;
        await using var cmd = _db.CreateCommand(sql);
        cmd.Parameters.AddWithValue("sid", delta.SessionId);
        cmd.Parameters.AddWithValue("owner", owner);
        cmd.Parameters.AddWithValue("in", delta.InputTokens);
        cmd.Parameters.AddWithValue("out", delta.OutputTokens);
        cmd.Parameters.AddWithValue("cr", delta.CacheReadTokens);
        cmd.Parameters.AddWithValue("cc", delta.CacheCreationTokens);
        cmd.Parameters.AddWithValue("cost", delta.CostUsd);
        cmd.Parameters.AddWithValue("est", estimated);
        cmd.Parameters.AddWithValue("auth", (object?)authMode ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
        return true;
    }

    public async Task AddResourceSampleAsync(string sessionId, string owner, SessionResourceSample sample, CancellationToken ct = default)
    {
        // Counters are cumulative per pod: add only the positive delta against the previous
        // snapshot. A counter that went backwards means a fresh pod (resume), whose full
        // value is the delta. Memory is a gauge — keep the latest value and the peak.
        const string sql = """
            INSERT INTO session_usage
                (session_id, owner, cpu_seconds, memory_bytes, peak_memory_bytes, rx_bytes, tx_bytes,
                 last_cpu_seconds, last_rx_bytes, last_tx_bytes, updated_at)
            VALUES (@sid, @owner, @cpu, @mem, @mem, @rx, @tx, @cpu, @rx, @tx, now())
            ON CONFLICT (session_id) DO UPDATE SET
                cpu_seconds       = session_usage.cpu_seconds +
                                    CASE WHEN @cpu >= session_usage.last_cpu_seconds
                                         THEN @cpu - session_usage.last_cpu_seconds ELSE @cpu END,
                rx_bytes          = session_usage.rx_bytes +
                                    CASE WHEN @rx >= session_usage.last_rx_bytes
                                         THEN @rx - session_usage.last_rx_bytes ELSE @rx END,
                tx_bytes          = session_usage.tx_bytes +
                                    CASE WHEN @tx >= session_usage.last_tx_bytes
                                         THEN @tx - session_usage.last_tx_bytes ELSE @tx END,
                memory_bytes      = @mem,
                peak_memory_bytes = GREATEST(session_usage.peak_memory_bytes, @mem),
                last_cpu_seconds  = @cpu,
                last_rx_bytes     = @rx,
                last_tx_bytes     = @tx,
                updated_at        = now();
            """;
        await using var cmd = _db.CreateCommand(sql);
        cmd.Parameters.AddWithValue("sid", sessionId);
        cmd.Parameters.AddWithValue("owner", owner);
        cmd.Parameters.AddWithValue("cpu", sample.CpuSeconds);
        cmd.Parameters.AddWithValue("mem", sample.MemoryBytes);
        cmd.Parameters.AddWithValue("rx", sample.RxBytes);
        cmd.Parameters.AddWithValue("tx", sample.TxBytes);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<(string? Owner, string? AuthMode)> ResolveSessionAsync(string sessionId, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand("SELECT owner, auth_mode FROM sessions WHERE id = @id");
        cmd.Parameters.AddWithValue("id", sessionId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return (null, null);
        return (r.IsDBNull(0) ? null : r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1));
    }

    private const string SelectBase = """
        SELECT u.session_id, u.owner, s.title,
               u.input_tokens, u.output_tokens, u.cache_read_tokens, u.cache_creation_tokens,
               u.cost_usd, u.estimated_cost_usd, u.auth_mode, u.created_at, u.updated_at,
               u.cpu_seconds, u.memory_bytes, u.peak_memory_bytes, u.rx_bytes, u.tx_bytes
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
                   COALESCE(SUM(cost_usd), 0),
                   COALESCE(SUM(estimated_cost_usd), 0),
                   COALESCE(SUM(CASE WHEN auth_mode = 'ApiKey' OR cost_usd > 0
                                     THEN 0 ELSE estimated_cost_usd END), 0),
                   COALESCE(SUM(cpu_seconds), 0),
                   COALESCE(SUM(rx_bytes), 0),
                   COALESCE(SUM(tx_bytes), 0)
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
            EstimatedCostUsd = r.GetDouble(6),
            SubscriptionEstimatedCostUsd = r.GetDouble(7),
            CpuSeconds = r.GetDouble(8),
            RxBytes = r.GetInt64(9),
            TxBytes = r.GetInt64(10),
            From = from, To = to
        };
    }

    public async Task<double> MonthToDateApiCostAsync(string owner, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand(
            "SELECT cost_usd FROM usage_monthly WHERE owner = @owner AND month = date_trunc('month', now())::date");
        cmd.Parameters.AddWithValue("owner", owner);
        return await cmd.ExecuteScalarAsync(ct) is double d ? d : 0;
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
        EstimatedCostUsd = r.GetDouble(8),
        AuthMode = r.IsDBNull(9) ? null : r.GetString(9),
        CreatedAt = r.GetDateTime(10),
        UpdatedAt = r.GetDateTime(11),
        CpuSeconds = r.GetDouble(12),
        MemoryBytes = r.GetInt64(13),
        PeakMemoryBytes = r.GetInt64(14),
        RxBytes = r.GetInt64(15),
        TxBytes = r.GetInt64(16)
    };
}
