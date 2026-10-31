using Npgsql;

namespace AgentHub.Api.Persistence;

/// <summary>
/// Serialises the startup schema initialisation across replicas (#371). Each store creates its
/// tables with <c>CREATE ... IF NOT EXISTS</c>, which is not safe against itself: two replicas
/// starting on a fresh database both pass the existence check, and the slower one fails on
/// <c>pg_type_typname_nsp_index</c> and crashes. The migrations of #367/#368 also take table
/// locks in their own transactions; one replica at a time means they never meet another
/// replica's migration halfway.
/// </summary>
public static class SchemaMigrationLock
{
    // Arbitrary but fixed: every replica, of every version that has this lock, has to pick the
    // same key. It shares the advisory-lock space with UserRoleStore's fixed key and the
    // hashtextextended keys of the session locks, and differs from all of them.
    private const long Key = 0x736368656D61; // "schema"

    /// <summary>
    /// Longer than any migration should take, so only a holder that is stuck ends the wait. The
    /// chart's startup probe allows a little more, so this error, not a probe kill, ends it.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Runs <paramref name="migrate"/> while holding the lock, on a connection of its own so the
    /// stores' pooled connections stay free. The lock is released and the connection closed
    /// even when <paramref name="migrate"/> throws; a replica that dies while holding it loses
    /// the connection, which releases it too.
    /// </summary>
    public static async Task RunAsync(IConfiguration configuration, ILogger logger, Func<Task> migrate,
        TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var connectionString = configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("ConnectionStrings:Postgres is missing.");
        // Unpooled: a pooled connection keeps a session-level lock until it is handed out again.
        await using var conn = new NpgsqlConnection(
            new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
        await conn.OpenAsync(ct);
        await WaitAsync(conn, logger, timeout ?? DefaultTimeout, ct);
        try
        {
            await migrate();
        }
        finally
        {
            await using var unlock = new NpgsqlCommand("SELECT pg_advisory_unlock(@k)", conn);
            unlock.Parameters.AddWithValue("k", Key);
            await unlock.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    // Polls rather than blocking in pg_advisory_lock: a blocked call says nothing in the log, and
    // an operator looking at a replica that does not come up needs to see what it waits for.
    private static async Task WaitAsync(NpgsqlConnection conn, ILogger logger, TimeSpan timeout, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var logged = false;
        while (true)
        {
            await using (var cmd = new NpgsqlCommand("SELECT pg_try_advisory_lock(@k)", conn))
            {
                cmd.Parameters.AddWithValue("k", Key);
                if (await cmd.ExecuteScalarAsync(ct) is true) break;
            }
            var waited = DateTime.UtcNow - started;
            if (waited >= timeout)
                throw new TimeoutException(
                    $"Waited {timeout.TotalMinutes:0} min for the schema migration lock held by another instance. " +
                    "Its holder is stuck or has not finished a migration; check that instance's log and pg_locks (advisory, objid " +
                    $"{Key & 0xFFFFFFFF}).");
            if (!logged)
            {
                logger.LogInformation("Waiting for schema migration lock held by another instance");
                logged = true;
            }
            await Task.Delay(PollInterval, ct);
        }
        if (logged)
            logger.LogInformation("Acquired schema migration lock after {Seconds:0.0}s", (DateTime.UtcNow - started).TotalSeconds);
    }
}
