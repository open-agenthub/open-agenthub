using Npgsql;

namespace AgentHub.Api.Persistence;

/// <summary>A known app user (captured at login) plus their Slack preferences.</summary>
public sealed record AppUser(
    string Owner, string? Email, string? DisplayName,
    bool SlackEnabled, string? SlackChannelOverride);

/// <summary>A user row for the admin seat/user overview.</summary>
public sealed record AdminUser(
    string Owner, string? Email, string? DisplayName,
    bool Licensed, DateTime FirstSeen, DateTime UpdatedAt);

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
            -- Seat/license flag: every user who signs in gets a seat by default; an
            -- admin can revoke it. Added via migration so existing tables pick it up.
            ALTER TABLE app_users ADD COLUMN IF NOT EXISTS licensed BOOLEAN NOT NULL DEFAULT TRUE;
            """;
        await using var cmd = _db.CreateCommand(ddl);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>All known users, most recently active first — for the admin overview.</summary>
    public async Task<IReadOnlyList<AdminUser>> ListAsync(CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand(
            "SELECT owner, email, display_name, licensed, first_seen, updated_at FROM app_users ORDER BY updated_at DESC");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<AdminUser>();
        while (await r.ReadAsync(ct))
            list.Add(new AdminUser(
                r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
                r.GetBoolean(3), r.GetDateTime(4), r.GetDateTime(5)));
        return list;
    }

    /// <summary>Number of users currently holding a seat (licensed = true).</summary>
    public async Task<int> CountLicensedAsync(CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand("SELECT COUNT(*) FROM app_users WHERE licensed");
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    /// <summary>Grants or revokes a user's seat. Returns false if the user is unknown.</summary>
    public async Task<bool> SetLicensedAsync(string owner, bool licensed, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand(
            "UPDATE app_users SET licensed = @l, updated_at = now() WHERE owner = @o");
        cmd.Parameters.AddWithValue("l", licensed);
        cmd.Parameters.AddWithValue("o", owner);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    /// <summary>True if the user holds a seat (unknown users are treated as unlicensed).</summary>
    public async Task<bool> IsLicensedAsync(string owner, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand("SELECT licensed FROM app_users WHERE owner = @o");
        cmd.Parameters.AddWithValue("o", owner);
        return await cmd.ExecuteScalarAsync(ct) is bool b && b;
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
