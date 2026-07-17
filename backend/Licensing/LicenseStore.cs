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
