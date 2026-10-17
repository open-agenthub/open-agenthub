using Npgsql;

namespace AgentHub.Api.Services;

/// <summary>
/// Per-login advisory lock for the refresher. Two replicas that both find a login due would
/// otherwise redeem the same refresh token; with rotation one of them is answered invalid_grant
/// and, worse, could mark a working login as needing a new sign-in.
/// </summary>
public sealed class PostgresProviderLoginLock : IProviderLoginLock
{
    private readonly NpgsqlDataSource _database;

    public PostgresProviderLoginLock(IConfiguration configuration)
    {
        _database = NpgsqlDataSource.Create(configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("ConnectionStrings:Postgres is missing."));
    }

    public async Task<IAsyncDisposable?> TryAcquireAsync(string key, CancellationToken ct = default)
    {
        var connection = await _database.OpenConnectionAsync(ct);
        try
        {
            // Key space 2: 0 is the browser lock, 1 the expiry sweep; a login key never collides
            // with a session id there, but a separate space makes that independent of the formats.
            await using var command = new NpgsqlCommand(
                "SELECT pg_try_advisory_lock(hashtextextended(@key, 2))", connection);
            command.Parameters.AddWithValue("key", key);
            if (await command.ExecuteScalarAsync(ct) is true) return new Held(connection, key);
            await connection.DisposeAsync();
            return null;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private sealed class Held(NpgsqlConnection connection, string key) : IAsyncDisposable
    {
        private int _disposed;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try
            {
                await using var command = new NpgsqlCommand(
                    "SELECT pg_advisory_unlock(hashtextextended(@key, 2))", connection);
                command.Parameters.AddWithValue("key", key);
                await command.ExecuteNonQueryAsync();
            }
            finally
            {
                await connection.DisposeAsync();
            }
        }
    }
}
