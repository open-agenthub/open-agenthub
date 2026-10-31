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
    // Arbitrary but fixed: every replica has to pick the same advisory-lock key.
    private const long Key = 0x736368656D61; // "schema"

    /// <summary>
    /// Waits for the lock on a connection of its own, so the stores' pooled connections stay
    /// free. A replica that dies while holding it closes that connection, which releases it.
    /// </summary>
    public static async Task<IAsyncDisposable> AcquireAsync(IConfiguration configuration, CancellationToken ct = default)
    {
        var connectionString = configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("ConnectionStrings:Postgres is missing.");
        // Unpooled: a pooled connection keeps a session-level lock until it is handed out again.
        var conn = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
        try
        {
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand("SELECT pg_advisory_lock(@k)", conn);
            cmd.Parameters.AddWithValue("k", Key);
            await cmd.ExecuteNonQueryAsync(ct);
            return new Held(conn);
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }

    private sealed class Held(NpgsqlConnection conn) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var cmd = new NpgsqlCommand("SELECT pg_advisory_unlock(@k)", conn);
                cmd.Parameters.AddWithValue("k", Key);
                await cmd.ExecuteNonQueryAsync();
            }
            finally { await conn.DisposeAsync(); }
        }
    }
}
