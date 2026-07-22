// -----------------------------------------------------------------------------
// Open AgentHub Enterprise Edition — Slack integration.
// Part of the Enterprise Edition; NOT covered by the AGPL-3.0 license of the
// open-core. Source-available under the Open AgentHub Enterprise License
// (see ee/LICENSE); a valid subscription is required for production use.
// -----------------------------------------------------------------------------
using Npgsql;

namespace AgentHub.Api.Ee.Slack;

public sealed record SlackThread(string SessionId, string Owner, string Channel, string ThreadTs, int PostedLen, string? StatusTs = null);

/// <summary>Maps a session to its Slack thread and tracks how much transcript was already posted.</summary>
public sealed class SlackThreadStore
{
    private readonly NpgsqlDataSource _db;

    public SlackThreadStore(IConfiguration cfg)
    {
        var cs = cfg.GetConnectionString("Postgres")
                 ?? throw new InvalidOperationException("ConnectionStrings:Postgres is missing.");
        _db = NpgsqlDataSource.Create(cs);
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        const string ddl = """
            CREATE TABLE IF NOT EXISTS slack_threads (
                session_id  TEXT PRIMARY KEY,
                owner       TEXT NOT NULL,
                channel     TEXT NOT NULL,
                thread_ts   TEXT NOT NULL,
                posted_len  INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_slack_threads_ts ON slack_threads(thread_ts);
            ALTER TABLE slack_threads ADD COLUMN IF NOT EXISTS status_ts TEXT;
            """;
        await using var cmd = _db.CreateCommand(ddl);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<SlackThread?> GetBySessionAsync(string sessionId, CancellationToken ct = default)
        => await QueryOne("WHERE session_id = @p", "p", sessionId, ct);

    public async Task<SlackThread?> GetByThreadTsAsync(string threadTs, CancellationToken ct = default)
        => await QueryOne("WHERE thread_ts = @p", "p", threadTs, ct);

    public async Task UpsertAsync(SlackThread t, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO slack_threads (session_id, owner, channel, thread_ts, posted_len, status_ts)
            VALUES (@id, @owner, @channel, @ts, @len, @status)
            ON CONFLICT (session_id) DO UPDATE SET
                owner = EXCLUDED.owner, channel = EXCLUDED.channel,
                thread_ts = EXCLUDED.thread_ts, posted_len = EXCLUDED.posted_len,
                status_ts = EXCLUDED.status_ts;
            """;
        await using var cmd = _db.CreateCommand(sql);
        cmd.Parameters.AddWithValue("id", t.SessionId);
        cmd.Parameters.AddWithValue("owner", t.Owner);
        cmd.Parameters.AddWithValue("channel", t.Channel);
        cmd.Parameters.AddWithValue("ts", t.ThreadTs);
        cmd.Parameters.AddWithValue("len", t.PostedLen);
        cmd.Parameters.AddWithValue("status", (object?)t.StatusTs ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SetPostedLenAsync(string sessionId, int len, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand("UPDATE slack_threads SET posted_len = @len WHERE session_id = @id");
        cmd.Parameters.AddWithValue("len", len);
        cmd.Parameters.AddWithValue("id", sessionId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Sets (or clears, with null) the ts of the "working…" status message.</summary>
    public async Task SetStatusTsAsync(string sessionId, string? ts, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand("UPDATE slack_threads SET status_ts = @ts WHERE session_id = @id");
        cmd.Parameters.AddWithValue("ts", (object?)ts ?? DBNull.Value);
        cmd.Parameters.AddWithValue("id", sessionId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<SlackThread?> QueryOne(string where, string p, string v, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand($"SELECT session_id, owner, channel, thread_ts, posted_len, status_ts FROM slack_threads {where}");
        cmd.Parameters.AddWithValue(p, v);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct)
            ? new SlackThread(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4),
                await r.IsDBNullAsync(5, ct) ? null : r.GetString(5))
            : null;
    }
}
