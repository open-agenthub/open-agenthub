using System.Globalization;
using System.Security.Cryptography;
using Npgsql;

namespace AgentHub.Api.Chat;

/// <summary>
/// Short-lived one-shot codes that link a chat identity to an app user: Telegram
/// deep-link /start codes (8 hex chars) and Signal verification codes (6 digits,
/// typed by the user). One active code per owner+purpose; codes expire after
/// 10 minutes and are consumed exactly once.
/// </summary>
public sealed class ChatLinkCodeStore
{
    private readonly NpgsqlDataSource _db;

    public ChatLinkCodeStore(IConfiguration cfg)
    {
        var cs = cfg.GetConnectionString("Postgres")
                 ?? throw new InvalidOperationException("ConnectionStrings:Postgres is missing.");
        _db = NpgsqlDataSource.Create(cs);
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        const string ddl = """
            CREATE TABLE IF NOT EXISTS chat_link_codes (
                code       TEXT PRIMARY KEY,
                owner      TEXT NOT NULL,
                purpose    TEXT NOT NULL,
                payload    TEXT,
                created_at TIMESTAMPTZ NOT NULL DEFAULT now()
            );
            """;
        await using var cmd = _db.CreateCommand(ddl);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Creates a fresh code for owner+purpose (invalidating any previous one) and returns it.
    /// Purpose "signal-verify" gets 6 digits (easy to type); everything else 8 hex chars
    /// (opaque enough for a t.me deep link).
    /// </summary>
    public async Task<string> CreateAsync(string owner, string purpose, string? payload = null, CancellationToken ct = default)
    {
        // The code column is a global PK — a fresh code can collide with another user's
        // live code (realistic for 6-digit verify codes). Regenerate and retry, max 3 attempts.
        for (var attempt = 1; ; attempt++)
        {
            var code = NewCode(purpose);
            try
            {
                await InsertFreshAsync(owner, purpose, code, payload, ct);
                return code;
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation && attempt < 3)
            {
                // collision with another owner's code — roll the dice again
            }
        }
    }

    private static string NewCode(string purpose) => purpose == "signal-verify"
        ? RandomNumberGenerator.GetInt32(100000, 1000000).ToString(CultureInfo.InvariantCulture)
        : Guid.NewGuid().ToString("n")[..8];

    /// <summary>Delete-then-insert in one transaction: exactly one active code per owner+purpose.</summary>
    private async Task InsertFreshAsync(string owner, string purpose, string code, string? payload, CancellationToken ct)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using (var del = new NpgsqlCommand(
            "DELETE FROM chat_link_codes WHERE owner = @o AND purpose = @p", conn, tx))
        {
            del.Parameters.AddWithValue("o", owner);
            del.Parameters.AddWithValue("p", purpose);
            await del.ExecuteNonQueryAsync(ct);
        }

        await using (var ins = new NpgsqlCommand(
            "INSERT INTO chat_link_codes (code, owner, purpose, payload) VALUES (@c, @o, @p, @pl)", conn, tx))
        {
            ins.Parameters.AddWithValue("c", code);
            ins.Parameters.AddWithValue("o", owner);
            ins.Parameters.AddWithValue("p", purpose);
            ins.Parameters.AddWithValue("pl", (object?)payload ?? DBNull.Value);
            await ins.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// Atomically consumes a live (less than 10 minutes old) code — the DELETE … RETURNING
    /// makes double-spending impossible. Null when the code is unknown, expired, already
    /// used or was created for a different purpose.
    /// </summary>
    public async Task<(string Owner, string? Payload)?> ConsumeAsync(string code, string purpose, CancellationToken ct = default)
    {
        // Cheap hygiene: sweep expired rows so the table never accumulates dead codes.
        await using (var sweep = _db.CreateCommand(
            "DELETE FROM chat_link_codes WHERE created_at <= now() - interval '10 minutes'"))
        {
            await sweep.ExecuteNonQueryAsync(ct);
        }

        const string sql = """
            DELETE FROM chat_link_codes
            WHERE code = @c AND purpose = @p AND created_at > now() - interval '10 minutes'
            RETURNING owner, payload
            """;
        await using var cmd = _db.CreateCommand(sql);
        cmd.Parameters.AddWithValue("c", code);
        cmd.Parameters.AddWithValue("p", purpose);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return (r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1));
    }
}
