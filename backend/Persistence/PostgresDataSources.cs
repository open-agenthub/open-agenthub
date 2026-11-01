using System.Collections.Concurrent;
using System.Data.Common;
using Npgsql;

namespace AgentHub.Api.Persistence;

/// <summary>
/// The process's Postgres pools, shared by every store (#377). Each store used to build its own
/// <see cref="NpgsqlDataSource"/>, and every data source is a pool of its own: one idle connection
/// per store stayed open after startup (29 per replica) and a burst of requests left about 80
/// behind, which ran a managed database with max_connections=100 out of clients at two replicas.
/// </summary>
/// <remarks>
/// Keyed by connection string rather than registered in the container, so the stores keep their
/// <c>IConfiguration</c> constructors and the tests that build them by hand share the same pool
/// per schema — the behaviour <c>new NpgsqlConnection(cs)</c> has always had.
/// </remarks>
public static class PostgresDataSources
{
    public const string MaxPoolSizeKey = "Postgres:MaxPoolSize";
    public const int DefaultMaxPoolSize = 20;
    public const string LockPoolSizeKey = "Postgres:LockPoolSize";
    // Browser starts release their lock before readiness polling. This pool still bounds
    // concurrent critical sections and stop cleanup; 20 + 10 + 2 per replica keeps two
    // replicas and a surge pod below a managed database's 100 connections.
    public const int DefaultLockPoolSize = 10;

    // Keyed by the pool's role as well: with an Application Name in the operator's connection
    // string both roles build the same string, and the lock pool would silently be the shared one.
    private static readonly ConcurrentDictionary<(string Role, string ConnectionString), Lazy<NpgsqlDataSource>> Pools = new();

    /// <summary>The pool every store runs its queries on.</summary>
    public static NpgsqlDataSource Shared(IConfiguration configuration) =>
        Get("shared", ConnectionString(configuration, "open-agenthub"));

    /// <summary>
    /// A second, small pool for connections held for the lifetime of a session-level advisory lock.
    /// A waiter in <c>pg_advisory_lock</c> holds its connection while it waits. In the shared pool,
    /// enough waiters for one session's browser lock would take every connection, and the holder
    /// could not get one for the queries it has to run before it releases: nothing moves until
    /// Npgsql's acquire timeout fails them all. Here, a lock request beyond the pool's size waits
    /// for a lock connection (up to the connection string's Timeout, 15 s by default), and the
    /// stores' queries are not affected.
    /// </summary>
    public static NpgsqlDataSource Locks(IConfiguration configuration) =>
        Get("locks", LockConnectionString(configuration));

    /// <summary>
    /// <c>Postgres:MaxPoolSize</c> wins, then a pool size written into the connection string, then
    /// <see cref="DefaultMaxPoolSize"/>; Npgsql's own default of 100 per pool is the whole of
    /// max_connections on many managed plans. The application name tells the two pools apart in
    /// pg_stat_activity unless the operator chose one.
    /// </summary>
    public static string ConnectionString(IConfiguration configuration, string applicationName)
    {
        var connectionString = Configured(configuration);
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (Size(configuration, MaxPoolSizeKey) is { } size)
        {
            if (size < builder.MinPoolSize)
                throw new InvalidOperationException(
                    $"{MaxPoolSizeKey} is {size}, below the connection string's Minimum Pool Size of {builder.MinPoolSize}.");
            builder.MaxPoolSize = size;
        }
        else if (!Names(connectionString, "maximumpoolsize", "maxpoolsize"))
        {
            // A connection string that only raises the minimum used to get Npgsql's 100 as the
            // maximum; a default below its minimum would make every connection attempt fail.
            builder.MaxPoolSize = Math.Max(DefaultMaxPoolSize, builder.MinPoolSize);
        }
        if (!Names(connectionString, "applicationname")) builder.ApplicationName = applicationName;
        return builder.ConnectionString;
    }

    /// <summary>
    /// <c>Postgres:LockPoolSize</c>, else <see cref="DefaultLockPoolSize"/>. A pool size in the
    /// connection string is meant for the queries and does not apply here, and neither does a
    /// minimum: lock connections open on demand and need not stay idle.
    /// </summary>
    public static string LockConnectionString(IConfiguration configuration)
    {
        var connectionString = Configured(configuration);
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            MinPoolSize = 0,
            MaxPoolSize = Size(configuration, LockPoolSizeKey) ?? DefaultLockPoolSize
        };
        if (!Names(connectionString, "applicationname")) builder.ApplicationName = "open-agenthub-locks";
        return builder.ConnectionString;
    }

    private static string Configured(IConfiguration configuration) =>
        configuration.GetConnectionString("Postgres")
        ?? throw new InvalidOperationException("ConnectionStrings:Postgres is missing.");

    private static int? Size(IConfiguration configuration, string key)
    {
        var configured = configuration[key];
        if (string.IsNullOrWhiteSpace(configured)) return null;
        if (!int.TryParse(configured, out var size) || size < 1)
            throw new InvalidOperationException($"{key} must be a positive number, not '{configured}'.");
        return size;
    }

    private static NpgsqlDataSource Get(string role, string connectionString) =>
        Pools.GetOrAdd((role, connectionString), key => new Lazy<NpgsqlDataSource>(() => NpgsqlDataSource.Create(key.ConnectionString))).Value;

    // NpgsqlConnectionStringBuilder cannot tell a value someone wrote from its default, so the
    // keys are read off the raw string, with Npgsql's spelling variants folded together.
    private static bool Names(string connectionString, params string[] keys)
    {
        var raw = new DbConnectionStringBuilder { ConnectionString = connectionString };
        return raw.Keys.Cast<string>()
            .Select(key => key.Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant())
            .Any(keys.Contains);
    }
}
