using Npgsql;

namespace AgentHub.Api.Services;

/// <summary>
/// A per-session lock the expiry sweep takes before deleting, so two backend replicas that list
/// the same due session do not both run the deletion. Non-blocking on purpose: a replica that
/// finds the lock taken has nothing to wait for — the holder is deleting the session.
/// </summary>
public interface ISessionExpiryLock
{
    /// <summary>The lock, or null when another holder has it.</summary>
    Task<IAsyncDisposable?> TryAcquireAsync(string sessionId, CancellationToken ct = default);
}

public sealed class PostgresSessionExpiryLock : ISessionExpiryLock
{
    private readonly NpgsqlDataSource _database;

    public PostgresSessionExpiryLock(IConfiguration configuration)
    {
        _database = NpgsqlDataSource.Create(configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("ConnectionStrings:Postgres is missing."));
    }

    public async Task<IAsyncDisposable?> TryAcquireAsync(string sessionId, CancellationToken ct = default)
    {
        var connection = await _database.OpenConnectionAsync(ct);
        try
        {
            // Key space 1, not 0: the browser lock hashes the same session id with 0, and the
            // two must not contend — a browser reconcile holding its lock is no reason to skip
            // an expiry, and vice versa.
            await using var command = new NpgsqlCommand(
                "SELECT pg_try_advisory_lock(hashtextextended(@session, 1))", connection);
            command.Parameters.AddWithValue("session", sessionId);
            var acquired = await command.ExecuteScalarAsync(ct) is true;
            if (!acquired)
            {
                await connection.DisposeAsync();
                return null;
            }
            return new AdvisoryLock(connection, sessionId);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private sealed class AdvisoryLock(NpgsqlConnection connection, string sessionId) : IAsyncDisposable
    {
        private int _disposed;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try
            {
                await using var command = new NpgsqlCommand(
                    "SELECT pg_advisory_unlock(hashtextextended(@session, 1))", connection);
                command.Parameters.AddWithValue("session", sessionId);
                await command.ExecuteNonQueryAsync();
            }
            finally
            {
                await connection.DisposeAsync();
            }
        }
    }
}
