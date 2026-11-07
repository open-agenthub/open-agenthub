using System.Security.Cryptography;
using Npgsql;

namespace AgentHub.Api.Persistence;

/// <summary>Which way a transfer link moves a conversation.</summary>
public enum SessionTransferDirection { TakeLocal, HandBack }

/// <summary>What a transfer link was minted for. The link itself is never stored.</summary>
public sealed record SessionTransferTicket(
    string Owner, string SessionId, SessionTransferDirection Direction, string Directory, bool Resume,
    DateTimeOffset ExpiresAt);

public interface ISessionTransferTicketStore
{
    /// <summary>Stores a new ticket and returns the secret that redeems it — the only copy.</summary>
    Task<string> MintAsync(SessionTransferTicket ticket, CancellationToken ct = default);

    /// <summary>
    /// Redeems a ticket exactly once. Null for an unknown, expired, already used or wrong-direction
    /// secret — the caller answers all of them the same, so a probe learns nothing from the code.
    /// </summary>
    Task<SessionTransferTicket?> RedeemAsync(string secret, SessionTransferDirection direction, CancellationToken ct = default);
}

/// <summary>
/// Short-lived, single-use links for moving a conversation between the hub and a workstation
/// without a personal API token (docs/session-transfer.md). The remote MCP endpoint and the web app
/// cannot write files on the user's machine, so they hand out a link that a local <c>curl</c> uses.
///
/// Stored in Postgres rather than signed into the link: the backend runs as several replicas, a
/// link minted by one has to be honoured by the other, and single use needs a record that the first
/// redemption removes. Only the SHA-256 of the secret is kept, as for API tokens.
/// </summary>
public sealed class PostgresSessionTransferTicketStore(IConfiguration cfg) : ISessionTransferTicketStore
{
    private readonly NpgsqlDataSource _db = PostgresDataSources.Shared(cfg);

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        const string ddl = """
            CREATE TABLE IF NOT EXISTS session_transfer_tickets (
                secret_hash TEXT PRIMARY KEY,
                owner       TEXT NOT NULL,
                session_id  TEXT NOT NULL,
                direction   TEXT NOT NULL,
                directory   TEXT NOT NULL,
                resume      BOOLEAN NOT NULL,
                expires_at  TIMESTAMPTZ NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_session_transfer_tickets_expiry ON session_transfer_tickets(expires_at);
            """;
        await SchemaScript.ApplyAsync(_db, nameof(PostgresSessionTransferTicketStore), ddl, ct);
    }

    public async Task<string> MintAsync(SessionTransferTicket ticket, CancellationToken ct = default)
    {
        var secret = "oat_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        // Expired rows are removed here rather than by a timer: minting is the only thing that adds
        // rows, so the table never holds more than the links of the last few minutes.
        const string sql = """
            DELETE FROM session_transfer_tickets WHERE expires_at < now();
            INSERT INTO session_transfer_tickets (secret_hash, owner, session_id, direction, directory, resume, expires_at)
            VALUES (@hash, @owner, @session, @direction, @directory, @resume, @expires);
            """;
        await using var cmd = _db.CreateCommand(sql);
        cmd.Parameters.AddWithValue("hash", ApiTokenStore.Hash(secret));
        cmd.Parameters.AddWithValue("owner", ticket.Owner);
        cmd.Parameters.AddWithValue("session", ticket.SessionId);
        cmd.Parameters.AddWithValue("direction", ticket.Direction.ToString());
        cmd.Parameters.AddWithValue("directory", ticket.Directory);
        cmd.Parameters.AddWithValue("resume", ticket.Resume);
        cmd.Parameters.AddWithValue("expires", ticket.ExpiresAt.UtcDateTime);
        await cmd.ExecuteNonQueryAsync(ct);
        return secret;
    }

    public async Task<SessionTransferTicket?> RedeemAsync(string secret, SessionTransferDirection direction, CancellationToken ct = default)
    {
        // DELETE … RETURNING is the single use: of two concurrent redemptions only one gets the row.
        const string sql = """
            DELETE FROM session_transfer_tickets
            WHERE secret_hash = @hash AND direction = @direction AND expires_at > now()
            RETURNING owner, session_id, directory, resume, expires_at;
            """;
        await using var cmd = _db.CreateCommand(sql);
        cmd.Parameters.AddWithValue("hash", ApiTokenStore.Hash(secret));
        cmd.Parameters.AddWithValue("direction", direction.ToString());
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new SessionTransferTicket(r.GetString(0), r.GetString(1), direction, r.GetString(2), r.GetBoolean(3),
            new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(4), DateTimeKind.Utc)));
    }
}
