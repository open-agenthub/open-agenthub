// -----------------------------------------------------------------------------
// Open AgentHub Enterprise Edition — Slack integration.
// Part of the Enterprise Edition; NOT covered by the AGPL-3.0 license of the
// open-core. Source-available under the Open AgentHub Enterprise License
// (see ee/LICENSE); a valid subscription is required for production use.
// -----------------------------------------------------------------------------
using Npgsql;

namespace AgentHub.Api.Ee.Slack;

public sealed record SlackThread(string SessionId, string Owner, string Channel, string ThreadTs, string? StatusTs = null);

/// <summary>
/// Maps a session to its Slack thread. The table still has a <c>posted_len</c> column from a
/// design that streamed the transcript into the thread; nothing has written it for a long time
/// and the relay posts events, not transcript, so the column is left alone at its default.
/// </summary>
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
            -- Take the parent lock first: replica startup and cascades must use the same lock order.
            LOCK TABLE sessions IN SHARE ROW EXCLUSIVE MODE;
            CREATE TABLE IF NOT EXISTS slack_threads (
                session_id  TEXT PRIMARY KEY,
                owner       TEXT NOT NULL,
                channel     TEXT NOT NULL,
                thread_ts   TEXT NOT NULL,
                posted_len  INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_slack_threads_ts ON slack_threads(thread_ts);
            ALTER TABLE slack_threads ADD COLUMN IF NOT EXISTS status_ts TEXT;

            CREATE OR REPLACE FUNCTION archive_deleted_slack_thread() RETURNS TRIGGER
            LANGUAGE plpgsql SET search_path FROM CURRENT AS $archive$
            BEGIN
                -- A cascade has already removed the parent. Ordinary mapping edits must not
                -- mark a still-live session deleted or extend a previous marker's lifetime.
                IF NOT EXISTS (SELECT 1 FROM sessions WHERE id = OLD.session_id) THEN
                    INSERT INTO chat_deleted_sessions
                        (platform, session_id, owner, chat_id, thread_id)
                    VALUES ('slack', OLD.session_id, OLD.owner, OLD.channel, OLD.thread_ts)
                    ON CONFLICT (platform, session_id) DO NOTHING;
                END IF;
                RETURN OLD;
            END $archive$;
            CREATE OR REPLACE TRIGGER archive_deleted_slack_thread
                AFTER DELETE ON slack_threads
                FOR EACH ROW EXECUTE FUNCTION archive_deleted_slack_thread();

            DO $migration$
            BEGIN
                IF NOT EXISTS (
                    SELECT 1 FROM pg_constraint
                    WHERE conrelid = 'slack_threads'::regclass AND conname = 'slack_threads_session_fk'
                ) THEN
                    -- Block new orphan writes until the prune and constraint commit together.
                    LOCK TABLE slack_threads IN SHARE ROW EXCLUSIVE MODE;
                    DELETE FROM slack_threads metadata
                    WHERE NOT EXISTS (SELECT 1 FROM sessions WHERE id = metadata.session_id);
                    ALTER TABLE slack_threads ADD CONSTRAINT slack_threads_session_fk
                        FOREIGN KEY (session_id) REFERENCES sessions(id) ON DELETE CASCADE;
                END IF;
            END $migration$;
            """;
        await using var connection = await _db.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var cmd = new NpgsqlCommand(ddl, connection, transaction);
        await cmd.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
        await PruneDeletedAsync(ct);
    }

    public async Task<bool> IsDeletedThreadAsync(string channel, string threadTs, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand("""
            SELECT EXISTS (SELECT 1 FROM chat_deleted_sessions
            WHERE platform = 'slack' AND chat_id = @channel AND thread_id = @thread
              AND deleted_at > now() - interval '30 days')
            """);
        cmd.Parameters.AddWithValue("channel", channel);
        cmd.Parameters.AddWithValue("thread", threadTs);
        return await cmd.ExecuteScalarAsync(ct) is true;
    }

    public async Task PruneDeletedAsync(CancellationToken ct = default)
    {
        // Begin pruning before lookup expiry to leave scheduling margin for the 30-day limit.
        await using var cmd = _db.CreateCommand("""
            DELETE FROM chat_deleted_sessions
            WHERE platform = 'slack' AND deleted_at <= now() - interval '29 days'
            """);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<SlackThread?> GetBySessionAsync(string sessionId, CancellationToken ct = default)
        => await QueryOne("WHERE session_id = @p", "p", sessionId, ct);

    public async Task<SlackThread?> GetByThreadTsAsync(string threadTs, CancellationToken ct = default)
        => await QueryOne("WHERE thread_ts = @p", "p", threadTs, ct);

    public async Task UpsertAsync(SlackThread t, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO slack_threads (session_id, owner, channel, thread_ts, status_ts)
            VALUES (@id, @owner, @channel, @ts, @status)
            ON CONFLICT (session_id) DO UPDATE SET
                owner = EXCLUDED.owner, channel = EXCLUDED.channel,
                thread_ts = EXCLUDED.thread_ts,
                status_ts = EXCLUDED.status_ts;
            """;
        await using var cmd = _db.CreateCommand(sql);
        cmd.Parameters.AddWithValue("id", t.SessionId);
        cmd.Parameters.AddWithValue("owner", t.Owner);
        cmd.Parameters.AddWithValue("channel", t.Channel);
        cmd.Parameters.AddWithValue("ts", t.ThreadTs);
        cmd.Parameters.AddWithValue("status", (object?)t.StatusTs ?? DBNull.Value);
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
        await using var cmd = _db.CreateCommand($"SELECT session_id, owner, channel, thread_ts, status_ts FROM slack_threads {where}");
        cmd.Parameters.AddWithValue(p, v);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct)
            ? new SlackThread(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
                await r.IsDBNullAsync(4, ct) ? null : r.GetString(4))
            : null;
    }
}
