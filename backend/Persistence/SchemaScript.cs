using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace AgentHub.Api.Persistence;

/// <summary>
/// Runs a store's schema script only when it differs from the one last applied (#538).
/// </summary>
/// <remarks>
/// <para>
/// The stores' scripts are idempotent (<c>ADD COLUMN IF NOT EXISTS</c> and the like), but
/// idempotent is not free: <c>ALTER TABLE</c> takes ACCESS EXCLUSIVE before it finds out there is
/// nothing to do. Run on every start, a replacement backend queued behind any transaction holding
/// a row lock on <c>sessions</c>, every later read of the table queued behind it on the healthy
/// replicas too, and the new backend failed after the 30 s command timeout. Recording a hash of
/// each applied script means an unchanged schema runs no DDL at all; a changed script is applied
/// again as a whole, so nobody has to remember to bump a version number.
/// </para>
/// <para>
/// When a script does have to run, each attempt waits at most <see cref="SchemaScriptOptions.LockTimeout"/>
/// for a lock and is then rolled back and retried after a pause. A queued ACCESS EXCLUSIVE request
/// blocks every read that arrives after it, so the bound on that wait is the bound on how long a
/// migration can stall live traffic; the pause lets the queued readers through before the next try.
/// </para>
/// </remarks>
public static class SchemaScript
{
    // Keep diagnostic output closed to unknown database names; the source-audit proof
    // requires every schema caller to be registered here before it can ship.
    public static readonly IReadOnlySet<string> DiagnosticNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "AllowedAgentsStore",
        "ApiTokenStore",
        "ChatBindingStore",
        "ChatLinkCodeStore",
        "EphemeralApiMcpStore",
        "LibraryShareStore",
        "LicenseStore",
        "McpClientStore",
        "McpServerStore",
        "McpSigningKeyStore",
        "PermissionStore",
        "PortGrantStore",
        "PostgresBrowserLeaseStore",
        "PostgresExecutionUsageSnapshots",
        "PostgresKeyRepository",
        "PostgresModelCatalogStore",
        "PostgresProjectStore",
        "PostgresProviderSwitchStore",
        "PostgresSecretRequestStore",
        "PostgresSessionExecutionStore",
        "PostgresSessionFileRegistry",
        "PostgresSessionMessageStore",
        "PostgresSessionOrderStore",
        "PostgresSessionStore",
        "PostgresSessionTransferTicketStore",
        "PostgresSessionWorkspaceStore",
        "PostgresUsageStore",
        "PostgresWebhookTriggerStore",
        "SessionShareStore",
        "SingleUserOwnerStore",
        "SkillEmbeddingStore",
        "SkillStore",
        "SlackThreadStore",
        "UsageLimitStore",
        "UserDirectory",
        "UserGroupStore",
        "UserRoleStore",
    };

    public const string TrackingTable = "schema_scripts";

    /// <summary>Set by <see cref="SchemaMigrationLock"/> for the migrations it runs, which have no logger of their own.</summary>
    internal static readonly AsyncLocal<ILogger?> Logger = new();

    /// <summary>Set by <see cref="SchemaMigrationLock"/>: where its own budget ends, which no script's retries may pass.</summary>
    internal static readonly AsyncLocal<DateTime?> Deadline = new();

    public static async Task ApplyAsync(NpgsqlDataSource db, string name, string sql, CancellationToken ct = default,
        SchemaScriptOptions? options = null)
    {
        options ??= SchemaScriptOptions.Default;
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sql)));
        await using var connection = await db.OpenConnectionAsync(ct);
        if (await AppliedAsync(connection, name, ct) == hash) return;

        var log = Logger.Value;
        var budget = Deadline.Value is { } deadline && deadline - DateTime.UtcNow < options.Budget
            ? deadline - DateTime.UtcNow
            : options.Budget;
        var started = Stopwatch.StartNew();
        var pause = options.FirstPause;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await RunAsync(connection, name, sql, hash, options.LockTimeout, ct);
                if (attempt > 1)
                    log?.LogInformation("Schema script {Name} applied after {Attempts} attempts ({Seconds:0.0}s)",
                        name, attempt, started.Elapsed.TotalSeconds);
                return;
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.LockNotAvailable)
            {
                var holders = await LongTransactionsAsync(connection, ct);
                if (started.Elapsed + pause >= budget)
                    throw new TimeoutException(
                        $"Schema script {name} could not lock its tables within {Math.Max(0, budget.TotalSeconds):0} s " +
                        $"({attempt} attempts, {options.LockTimeout.TotalMilliseconds:0} ms each): another transaction " +
                        $"holds a conflicting lock. Open transactions: {holders}. End or wait for them and start again.", ex);
                log?.LogWarning("Schema script {Name} waited {LockTimeoutMs} ms for a table lock and backed off " +
                    "(attempt {Attempt}); retrying in {PauseMs} ms. Open transactions: {Holders}",
                    name, options.LockTimeout.TotalMilliseconds, attempt, pause.TotalMilliseconds, holders);
                await Task.Delay(pause, ct);
                pause = TimeSpan.FromTicks(Math.Min(pause.Ticks * 2, options.MaxPause.Ticks));
            }
        }
    }

    private static async Task<string?> AppliedAsync(NpgsqlConnection connection, string name, CancellationToken ct)
    {
        // Looked up before it is read: the first start of a version that tracks scripts must not
        // need DDL just to learn that it needs DDL. Neither query takes more than ACCESS SHARE.
        await using (var exists = new NpgsqlCommand($"SELECT to_regclass('{TrackingTable}') IS NOT NULL", connection))
            if (await exists.ExecuteScalarAsync(ct) is not true) return null;
        await using var command = new NpgsqlCommand($"SELECT sha256 FROM {TrackingTable} WHERE name = @name", connection);
        command.Parameters.AddWithValue("name", name);
        return await command.ExecuteScalarAsync(ct) as string;
    }

    private static async Task RunAsync(NpgsqlConnection connection, string name, string sql, string hash,
        TimeSpan lockTimeout, CancellationToken ct)
    {
        // One transaction for the script and its record: an attempt that times out leaves nothing
        // half-applied, and a script is never recorded without having run to the end.
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var setup = new NpgsqlCommand($"""
            SET LOCAL lock_timeout = {(long)lockTimeout.TotalMilliseconds};
            CREATE TABLE IF NOT EXISTS {TrackingTable} (
                name       TEXT PRIMARY KEY,
                sha256     TEXT NOT NULL,
                applied_at TIMESTAMPTZ NOT NULL DEFAULT now()
            );
            """, connection, transaction))
            await setup.ExecuteNonQueryAsync(ct);
        await using (var script = new NpgsqlCommand(sql, connection, transaction))
            await script.ExecuteNonQueryAsync(ct);
        await using (var record = new NpgsqlCommand($"""
            INSERT INTO {TrackingTable} (name, sha256) VALUES (@name, @hash)
            ON CONFLICT (name) DO UPDATE SET sha256 = EXCLUDED.sha256, applied_at = now()
            """, connection, transaction))
        {
            record.Parameters.AddWithValue("name", name);
            record.Parameters.AddWithValue("hash", hash);
            await record.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }

    // What an operator needs to find the holder: the lock-timeout error itself does not say who
    // holds the lock, and by the time someone looks the wait is gone from pg_locks. Query text is
    // left out because it can carry parameters inlined by other clients.
    private static async Task<string> LongTransactionsAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        try
        {
            await using var command = new NpgsqlCommand("""
                SELECT string_agg(format('pid %s (%s, %s, %s s)', pid, COALESCE(NULLIF(application_name, ''), '?'), state,
                                         floor(extract(epoch FROM now() - xact_start))), '; ' ORDER BY xact_start)
                FROM pg_stat_activity
                WHERE datname = current_database() AND pid <> pg_backend_pid() AND xact_start IS NOT NULL
                """, connection);
            return await command.ExecuteScalarAsync(ct) as string ?? "none visible";
        }
        catch (PostgresException)
        {
            return "not readable";
        }
    }
}

public sealed record SchemaScriptOptions(TimeSpan LockTimeout, TimeSpan FirstPause, TimeSpan MaxPause, TimeSpan Budget)
{
    /// <summary>
    /// Two seconds is what a migration may add to a live read. Pauses of up to five seconds give the
    /// readers queued behind an attempt at least twice its window, yet pick up a released lock
    /// within seconds during a rolling update. Three minutes keeps the attempts of
    /// one script well inside <see cref="SchemaMigrationLock.DefaultTimeout"/> and the chart's
    /// startup probe, so a replica that cannot migrate says why instead of being killed silently.
    /// </summary>
    public static readonly SchemaScriptOptions Default = new(
        LockTimeout: TimeSpan.FromSeconds(2), FirstPause: TimeSpan.FromMilliseconds(500),
        MaxPause: TimeSpan.FromSeconds(5), Budget: TimeSpan.FromMinutes(3));
}
