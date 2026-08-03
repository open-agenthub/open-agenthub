using System.Security.Cryptography;
using Npgsql;

namespace AgentHub.Api.Licensing;

/// <summary>Persistence for the activated enterprise license token.</summary>
public interface ILicenseStore
{
    Task InitializeAsync(CancellationToken ct = default);
    Task<string?> GetTokenAsync(CancellationToken ct = default);
    Task SetTokenAsync(string? token, CancellationToken ct = default);
}

/// <summary>
/// Stores the enterprise license token in the database (single row). The token is
/// activated through the admin UI rather than configured in the chart, so an operator
/// cannot simply flip a Helm value to unlock enterprise features.
/// </summary>
public sealed class LicenseStore : ILicenseStore
{
    private readonly NpgsqlDataSource _db;

    public LicenseStore(IConfiguration cfg)
    {
        var cs = cfg.GetConnectionString("Postgres")
                 ?? throw new InvalidOperationException("ConnectionStrings:Postgres is missing.");
        _db = NpgsqlDataSource.Create(cs);
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        const string ddl = """
            CREATE TABLE IF NOT EXISTS app_license (
                id             INTEGER PRIMARY KEY DEFAULT 1,
                token          TEXT,
                updated_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
                CONSTRAINT app_license_singleton CHECK (id = 1)
            );
            -- Last successful seat check-in to the license service (heartbeat). Added via
            -- migration so existing single-row tables pick it up.
            ALTER TABLE app_license ADD COLUMN IF NOT EXISTS last_report_at TIMESTAMPTZ;
            -- Stable per-instance secret sent at checkout so this instance can later pull
            -- its own license token from the service (self-service activation).
            ALTER TABLE app_license ADD COLUMN IF NOT EXISTS instance_key TEXT;
            -- Scheduled subscription cancellation, mirrored from the heartbeat response —
            -- shown in the admin UI so a cancellation is visible before features lapse.
            ALTER TABLE app_license ADD COLUMN IF NOT EXISTS cancel_at TIMESTAMPTZ;
            """;
        await using var cmd = _db.CreateCommand(ddl);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<string?> GetTokenAsync(CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand("SELECT token FROM app_license WHERE id = 1");
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    public async Task SetTokenAsync(string? token, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand("""
            INSERT INTO app_license (id, token, updated_at) VALUES (1, @t, now())
            ON CONFLICT (id) DO UPDATE SET token = EXCLUDED.token, updated_at = now();
            """);
        cmd.Parameters.AddWithValue("t", (object?)token ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// The stable secret identifying this instance to the license service. Generated once
    /// (32 random bytes, hex) on first use and persisted; sent at checkout and reused to
    /// self-activate via /api/license/claim. Safe under concurrency: the first writer wins.
    /// </summary>
    public async Task<string> GetOrCreateInstanceKeyAsync(CancellationToken ct = default)
    {
        await using (var get = _db.CreateCommand("SELECT instance_key FROM app_license WHERE id = 1"))
        {
            if (await get.ExecuteScalarAsync(ct) is string existing && !string.IsNullOrWhiteSpace(existing))
                return existing;
        }

        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        await using (var set = _db.CreateCommand("""
            INSERT INTO app_license (id, instance_key) VALUES (1, @k)
            ON CONFLICT (id) DO UPDATE SET instance_key = COALESCE(app_license.instance_key, EXCLUDED.instance_key);
            """))
        {
            set.Parameters.AddWithValue("k", key);
            await set.ExecuteNonQueryAsync(ct);
        }

        // Re-read: a racing caller may have inserted first; COALESCE kept whichever landed first.
        await using var reread = _db.CreateCommand("SELECT instance_key FROM app_license WHERE id = 1");
        return (string)(await reread.ExecuteScalarAsync(ct))!;
    }

    /// <summary>Scheduled subscription cancellation reported by the license service, or null.</summary>
    public async Task<DateTime?> GetCancelAtAsync(CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand("SELECT cancel_at FROM app_license WHERE id = 1");
        return await cmd.ExecuteScalarAsync(ct) as DateTime?;
    }

    public async Task SetCancelAtAsync(DateTime? whenUtc, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand("""
            INSERT INTO app_license (id, cancel_at) VALUES (1, @t)
            ON CONFLICT (id) DO UPDATE SET cancel_at = EXCLUDED.cancel_at;
            """);
        cmd.Parameters.AddWithValue("t", (object?)whenUtc ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>When the last successful seat check-in happened, or null if never.</summary>
    public async Task<DateTime?> GetLastReportAsync(CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand("SELECT last_report_at FROM app_license WHERE id = 1");
        return await cmd.ExecuteScalarAsync(ct) as DateTime?;
    }

    public async Task SetLastReportAsync(DateTime whenUtc, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand("""
            INSERT INTO app_license (id, last_report_at) VALUES (1, @t)
            ON CONFLICT (id) DO UPDATE SET last_report_at = EXCLUDED.last_report_at;
            """);
        cmd.Parameters.AddWithValue("t", whenUtc);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
