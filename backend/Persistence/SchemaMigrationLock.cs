using System.Diagnostics;
using System.Net.Sockets;
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
        var budget = timeout ?? DefaultTimeout;
        var started = Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(budget);
        try
        {
            await OpenWhenReadyAsync(conn, logger, deadline.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new TimeoutException($"Waited {budget.TotalSeconds:0} seconds for database to accept connections.");
        }
        // Connection recovery and lock contention share one budget: two independent ten-minute
        // waits would let the startup probe kill a replica that is making progress.
        await WaitAsync(conn, logger, budget - started.Elapsed, ct);
        // The stores apply their scripts without a logger of their own; this is how a replica's
        // log says which table it is waiting for (#538). Their lock retries share this budget:
        // with one of their own on top, a replica that waited for the lock and then for a table
        // could outlast the chart's startup probe and be killed without saying why.
        SchemaScript.Logger.Value = logger;
        SchemaScript.Deadline.Value = DateTime.UtcNow + (budget - started.Elapsed);
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

    private static async Task OpenWhenReadyAsync(NpgsqlConnection conn, ILogger logger, CancellationToken ct)
    {
        var delay = TimeSpan.FromMilliseconds(250);
        while (true)
        {
            try
            {
                await conn.OpenAsync(ct);
                return;
            }
            catch (Exception ex) when (IsStartupUnavailable(ex))
            {
                logger.LogInformation("waiting for database to accept connections; retrying in {DelayMs}ms", delay.TotalMilliseconds);
                await Task.Delay(delay, ct);
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 5000));
            }
        }
    }

    private static bool IsStartupUnavailable(Exception ex) =>
        ex is PostgresException { SqlState: PostgresErrorCodes.CannotConnectNow } ||
        ex is NpgsqlException { InnerException: SocketException { SocketErrorCode: SocketError.ConnectionRefused } };

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
