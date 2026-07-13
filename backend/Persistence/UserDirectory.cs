using Npgsql;

namespace AgentHub.Api.Persistence;

/// <summary>A known app user (captured at login) plus their Slack preferences.</summary>
public sealed record AppUser(
    string Owner, string? Email, string? DisplayName,
    bool SlackEnabled, string? SlackChannelOverride);

/// <summary>
/// Directory of users who have signed in (owner = preferred_username), with their
/// email (for Slack lookup) and per-user Slack routing preferences. Populated from
/// the OIDC claims on authenticated requests.
/// </summary>
public sealed class UserDirectory
{
    private readonly NpgsqlDataSource _db;

    public UserDirectory(IConfiguration cfg)
    {
        var cs = cfg.GetConnectionString("Postgres")
                 ?? throw new InvalidOperationException("ConnectionStrings:Postgres is missing.");
        _db = NpgsqlDataSource.Create(cs);
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        const string ddl = """
            CREATE TABLE IF NOT EXISTS app_users (
                owner              TEXT PRIMARY KEY,
                email              TEXT,
                display_name       TEXT,
                slack_enabled      BOOLEAN NOT NULL DEFAULT TRUE,
                slack_channel      TEXT,
                first_seen         TIMESTAMPTZ NOT NULL DEFAULT now(),
                updated_at         TIMESTAMPTZ NOT NULL DEFAULT now()
            );
            """;
        await using var cmd = _db.CreateCommand(ddl);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Upsert the identity seen at login. Keeps existing Slack prefs.</summary>
    public async Task RecordLoginAsync(string owner, string? email, string? displayName, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO app_users (owner, email, display_name)
            VALUES (@o, @e, @d)
            ON CONFLICT (owner) DO UPDATE SET
                email = COALESCE(EXCLUDED.email, app_users.email),
                display_name = COALESCE(EXCLUDED.display_name, app_users.display_name),
                updated_at = now();
            """;
        await using var cmd = _db.CreateCommand(sql);
        cmd.Parameters.AddWithValue("o", owner);
        cmd.Parameters.AddWithValue("e", (object?)email ?? DBNull.Value);
        cmd.Parameters.AddWithValue("d", (object?)displayName ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<AppUser?> GetAsync(string owner, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand(
            "SELECT owner, email, display_name, slack_enabled, slack_channel FROM app_users WHERE owner = @o");
        cmd.Parameters.AddWithValue("o", owner);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct)
            ? new AppUser(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2), r.GetBoolean(3), r.IsDBNull(4) ? null : r.GetString(4))
            : null;
    }

    public async Task SetSlackPrefsAsync(string owner, bool enabled, string? channelOverride, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO app_users (owner, slack_enabled, slack_channel)
            VALUES (@o, @en, @ch)
            ON CONFLICT (owner) DO UPDATE SET
                slack_enabled = EXCLUDED.slack_enabled,
                slack_channel = EXCLUDED.slack_channel,
                updated_at = now();
            """;
        await using var cmd = _db.CreateCommand(sql);
        cmd.Parameters.AddWithValue("o", owner);
        cmd.Parameters.AddWithValue("en", enabled);
        cmd.Parameters.AddWithValue("ch", string.IsNullOrWhiteSpace(channelOverride) ? DBNull.Value : channelOverride);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
