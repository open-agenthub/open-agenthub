// -----------------------------------------------------------------------------
// Open AgentHub Enterprise Edition — Admin-managed usage limits.
// Part of the Enterprise Edition; NOT covered by the AGPL-3.0 license of the
// open-core. Source-available under the Open AgentHub Enterprise License
// (see ee/LICENSE); a valid subscription is required for production use.
// -----------------------------------------------------------------------------
using Npgsql;

namespace AgentHub.Api.Ee.Usage;

/// <summary>One admin-configured limit row. Scope: "global" | "group" | "user".</summary>
public sealed record AdminUsageLimit(string Scope, string Target, double LimitUsd, DateTime UpdatedAt);

/// <summary>
/// Persists the admin-configured monthly API budgets: one optional global ceiling, plus
/// per-group and per-user overrides. The strictest applicable limit wins (resolved in
/// <see cref="EeUsageLimitProvider"/>).
/// </summary>
public sealed class UsageLimitStore
{
    public const string GlobalScope = "global";
    public const string GroupScope = "group";
    public const string UserScope = "user";

    private readonly NpgsqlDataSource _db;

    public UsageLimitStore(IConfiguration cfg)
    {
        var cs = cfg.GetConnectionString("Postgres")
                 ?? throw new InvalidOperationException("ConnectionStrings:Postgres is missing.");
        _db = NpgsqlDataSource.Create(cs);
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        const string ddl = """
            CREATE TABLE IF NOT EXISTS usage_limits (
                scope      TEXT NOT NULL CHECK (scope IN ('global', 'group', 'user')),
                target     TEXT NOT NULL DEFAULT '',
                limit_usd  DOUBLE PRECISION NOT NULL CHECK (limit_usd >= 0),
                updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
                PRIMARY KEY (scope, target)
            );
            """;
        await using var cmd = _db.CreateCommand(ddl);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<AdminUsageLimit>> ListAsync(CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand(
            "SELECT scope, target, limit_usd, updated_at FROM usage_limits ORDER BY scope, target");
        var list = new List<AdminUsageLimit>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new AdminUsageLimit(r.GetString(0), r.GetString(1), r.GetDouble(2), r.GetDateTime(3)));
        return list;
    }

    public async Task SetAsync(string scope, string target, double limitUsd, CancellationToken ct = default)
    {
        ValidateScope(scope, target);
        if (double.IsNaN(limitUsd) || double.IsInfinity(limitUsd) || limitUsd < 0)
            throw new ArgumentException("limitUsd must be a non-negative number.");
        const string sql = """
            INSERT INTO usage_limits (scope, target, limit_usd) VALUES (@s, @t, @l)
            ON CONFLICT (scope, target) DO UPDATE SET limit_usd = EXCLUDED.limit_usd, updated_at = now();
            """;
        await using var cmd = _db.CreateCommand(sql);
        cmd.Parameters.AddWithValue("s", scope);
        cmd.Parameters.AddWithValue("t", NormalizedTarget(scope, target));
        cmd.Parameters.AddWithValue("l", limitUsd);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> DeleteAsync(string scope, string target, CancellationToken ct = default)
    {
        ValidateScope(scope, target);
        await using var cmd = _db.CreateCommand(
            "DELETE FROM usage_limits WHERE scope = @s AND target = @t");
        cmd.Parameters.AddWithValue("s", scope);
        cmd.Parameters.AddWithValue("t", NormalizedTarget(scope, target));
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    /// <summary>The global limit, the user's own row, and rows for any of the user's groups.</summary>
    public async Task<IReadOnlyList<AdminUsageLimit>> ApplicableAsync(
        string owner, IReadOnlyCollection<string> groups, CancellationToken ct = default)
    {
        const string sql = """
            SELECT scope, target, limit_usd, updated_at FROM usage_limits
            WHERE (scope = 'global')
               OR (scope = 'user'  AND target = @owner)
               OR (scope = 'group' AND target = ANY(@groups))
            """;
        await using var cmd = _db.CreateCommand(sql);
        cmd.Parameters.AddWithValue("owner", owner);
        cmd.Parameters.AddWithValue("groups", groups.ToArray());
        var list = new List<AdminUsageLimit>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new AdminUsageLimit(r.GetString(0), r.GetString(1), r.GetDouble(2), r.GetDateTime(3)));
        return list;
    }

    private static void ValidateScope(string scope, string target)
    {
        if (scope is not (GlobalScope or GroupScope or UserScope))
            throw new ArgumentException($"Unknown scope '{scope}'.");
        if (scope is not GlobalScope && string.IsNullOrWhiteSpace(target))
            throw new ArgumentException("A target is required for group/user limits.");
    }

    private static string NormalizedTarget(string scope, string target)
        => scope == GlobalScope ? "" : target.Trim();
}
