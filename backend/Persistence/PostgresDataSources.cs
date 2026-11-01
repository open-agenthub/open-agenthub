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

    private static readonly ConcurrentDictionary<string, Lazy<NpgsqlDataSource>> Pools = new(StringComparer.Ordinal);

    /// <summary>The pool every store runs its queries on.</summary>
    public static NpgsqlDataSource Shared(IConfiguration configuration) =>
        Get(ConnectionString(configuration, "open-agenthub"));

    /// <summary>
    /// A second pool for connections held for the lifetime of a session-level advisory lock.
    /// A waiter in <c>pg_advisory_lock</c> holds its connection while it waits. In the shared pool,
    /// enough waiters for one session's browser lock would take every connection, and the holder
    /// could not get one for the queries it has to run before it releases: nothing moves until
    /// Npgsql's acquire timeout fails them all.
    /// </summary>
    public static NpgsqlDataSource Locks(IConfiguration configuration) =>
        Get(ConnectionString(configuration, "open-agenthub-locks"));

    /// <summary>
    /// <c>Postgres:MaxPoolSize</c> wins, then a pool size written into the connection string, then
    /// <see cref="DefaultMaxPoolSize"/>; Npgsql's own default of 100 per pool is the whole of
    /// max_connections on many managed plans. The application name tells the two pools apart in
    /// pg_stat_activity unless the operator chose one.
    /// </summary>
    public static string ConnectionString(IConfiguration configuration, string applicationName)
    {
        var connectionString = configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("ConnectionStrings:Postgres is missing.");
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var configured = configuration[MaxPoolSizeKey];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (!int.TryParse(configured, out var size) || size < 1)
                throw new InvalidOperationException($"{MaxPoolSizeKey} must be a positive number, not '{configured}'.");
            builder.MaxPoolSize = size;
        }
        else if (!Names(connectionString, "maximumpoolsize", "maxpoolsize"))
        {
            builder.MaxPoolSize = DefaultMaxPoolSize;
        }
        if (!Names(connectionString, "applicationname")) builder.ApplicationName = applicationName;
        return builder.ConnectionString;
    }

    private static NpgsqlDataSource Get(string connectionString) =>
        Pools.GetOrAdd(connectionString, cs => new Lazy<NpgsqlDataSource>(() => NpgsqlDataSource.Create(cs))).Value;

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
