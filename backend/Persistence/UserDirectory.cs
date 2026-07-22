using Npgsql;

namespace AgentHub.Api.Persistence;

/// <summary>A known app user (captured at login) plus their chat (Slack/Telegram/Signal) preferences.</summary>
public sealed record AppUser(
    string Owner, string? Email, string? DisplayName,
    bool SlackEnabled, string? SlackChannelOverride,
    string? TelegramChatId = null, bool TelegramForum = false, bool TelegramEnabled = true,
    string? SignalNumber = null, bool SignalVerified = false, bool SignalEnabled = true);

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
            -- Community chat integrations: per-user Telegram/Signal routing preferences.
            ALTER TABLE app_users ADD COLUMN IF NOT EXISTS telegram_chat_id TEXT;
            ALTER TABLE app_users ADD COLUMN IF NOT EXISTS telegram_forum BOOLEAN NOT NULL DEFAULT FALSE;
            ALTER TABLE app_users ADD COLUMN IF NOT EXISTS telegram_enabled BOOLEAN NOT NULL DEFAULT TRUE;
            ALTER TABLE app_users ADD COLUMN IF NOT EXISTS signal_number TEXT;
            ALTER TABLE app_users ADD COLUMN IF NOT EXISTS signal_verified BOOLEAN NOT NULL DEFAULT FALSE;
            ALTER TABLE app_users ADD COLUMN IF NOT EXISTS signal_enabled BOOLEAN NOT NULL DEFAULT TRUE;
            -- A Telegram chat / Signal number can only ever be bound to one user.
            CREATE UNIQUE INDEX IF NOT EXISTS app_users_telegram_chat_key ON app_users (telegram_chat_id) WHERE telegram_chat_id IS NOT NULL;
            CREATE UNIQUE INDEX IF NOT EXISTS app_users_signal_number_key ON app_users (signal_number) WHERE signal_number IS NOT NULL;
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

    // Shared column list + row mapping for all single-user lookups.
    private const string AppUserColumns =
        "owner, email, display_name, slack_enabled, slack_channel, " +
        "telegram_chat_id, telegram_forum, telegram_enabled, signal_number, signal_verified, signal_enabled";

    private static AppUser ReadAppUser(NpgsqlDataReader r) => new(
        r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
        r.GetBoolean(3), r.IsDBNull(4) ? null : r.GetString(4),
        r.IsDBNull(5) ? null : r.GetString(5), r.GetBoolean(6), r.GetBoolean(7),
        r.IsDBNull(8) ? null : r.GetString(8), r.GetBoolean(9), r.GetBoolean(10));

    private async Task<AppUser?> GetOneAsync(string where, string paramName, string paramValue, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand(
            $"SELECT {AppUserColumns} FROM app_users WHERE {where} LIMIT 1");
        cmd.Parameters.AddWithValue(paramName, paramValue);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadAppUser(r) : null;
    }

    public Task<AppUser?> GetAsync(string owner, CancellationToken ct = default)
        => GetOneAsync("owner = @o", "o", owner, ct);

    /// <summary>The user a Telegram chat is linked to, if any.</summary>
    public Task<AppUser?> GetByTelegramChatAsync(string chatId, CancellationToken ct = default)
        => GetOneAsync("telegram_chat_id = @c", "c", chatId, ct);

    /// <summary>The user a Signal number belongs to, if any.</summary>
    public Task<AppUser?> GetBySignalNumberAsync(string number, CancellationToken ct = default)
        => GetOneAsync("signal_number = @n", "n", number, ct);

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

    /// <summary>
    /// Links a Telegram chat (private chat or forum group) to the user. A chat binds to at
    /// most one user — the last /link wins. Returns true when the chat was taken over from
    /// another user (so the caller can mention the takeover in its reply).
    /// </summary>
    public async Task<bool> SetTelegramLinkAsync(string owner, string chatId, bool isForum, CancellationToken ct = default)
    {
        // Unlink-from-other + upsert in one transaction so a concurrent /link cannot
        // race us into the partial unique index.
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using var steal = new NpgsqlCommand(
            "UPDATE app_users SET telegram_chat_id = NULL, telegram_forum = FALSE, updated_at = now() " +
            "WHERE telegram_chat_id = @c AND owner <> @o", conn, tx);
        steal.Parameters.AddWithValue("c", chatId);
        steal.Parameters.AddWithValue("o", owner);
        var stolen = await steal.ExecuteNonQueryAsync(ct) > 0;

        const string sql = """
            INSERT INTO app_users (owner, telegram_chat_id, telegram_forum)
            VALUES (@o, @c, @f)
            ON CONFLICT (owner) DO UPDATE SET
                telegram_chat_id = EXCLUDED.telegram_chat_id,
                telegram_forum = EXCLUDED.telegram_forum,
                updated_at = now();
            """;
        await using var upsert = new NpgsqlCommand(sql, conn, tx);
        upsert.Parameters.AddWithValue("o", owner);
        upsert.Parameters.AddWithValue("c", chatId);
        upsert.Parameters.AddWithValue("f", isForum);
        await upsert.ExecuteNonQueryAsync(ct);

        await tx.CommitAsync(ct);
        return stolen;
    }

    /// <summary>Unlinks the user's Telegram chat. No-op for unknown users.</summary>
    public async Task ClearTelegramLinkAsync(string owner, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand(
            "UPDATE app_users SET telegram_chat_id = NULL, telegram_forum = FALSE, updated_at = now() WHERE owner = @o");
        cmd.Parameters.AddWithValue("o", owner);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SetTelegramEnabledAsync(string owner, bool enabled, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO app_users (owner, telegram_enabled)
            VALUES (@o, @en)
            ON CONFLICT (owner) DO UPDATE SET
                telegram_enabled = EXCLUDED.telegram_enabled,
                updated_at = now();
            """;
        await using var cmd = _db.CreateCommand(sql);
        cmd.Parameters.AddWithValue("o", owner);
        cmd.Parameters.AddWithValue("en", enabled);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Sets (or clears, with null) the user's Signal number. Changing the number invalidates
    /// verification; re-saving the same number keeps it. A number is exclusive to one user —
    /// returns false when another user already claimed it (caller maps this to 409).
    /// </summary>
    public async Task<bool> SetSignalNumberAsync(string owner, string? number, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO app_users (owner, signal_number)
            VALUES (@o, @n)
            ON CONFLICT (owner) DO UPDATE SET
                signal_number = EXCLUDED.signal_number,
                signal_verified = CASE
                    WHEN app_users.signal_number IS DISTINCT FROM EXCLUDED.signal_number THEN FALSE
                    ELSE app_users.signal_verified END,
                updated_at = now();
            """;
        await using var cmd = _db.CreateCommand(sql);
        cmd.Parameters.AddWithValue("o", owner);
        cmd.Parameters.AddWithValue("n", string.IsNullOrWhiteSpace(number) ? DBNull.Value : number);
        try
        {
            await cmd.ExecuteNonQueryAsync(ct);
            return true;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return false; // number already claimed by another user
        }
    }

    /// <summary>Marks the user's Signal number as (un)verified. No-op for unknown users.</summary>
    public async Task SetSignalVerifiedAsync(string owner, bool verified, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand(
            "UPDATE app_users SET signal_verified = @v, updated_at = now() WHERE owner = @o");
        cmd.Parameters.AddWithValue("v", verified);
        cmd.Parameters.AddWithValue("o", owner);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SetSignalEnabledAsync(string owner, bool enabled, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO app_users (owner, signal_enabled)
            VALUES (@o, @en)
            ON CONFLICT (owner) DO UPDATE SET
                signal_enabled = EXCLUDED.signal_enabled,
                updated_at = now();
            """;
        await using var cmd = _db.CreateCommand(sql);
        cmd.Parameters.AddWithValue("o", owner);
        cmd.Parameters.AddWithValue("en", enabled);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
