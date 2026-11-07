using AgentHub.Api.Persistence;
using Npgsql;

namespace AgentHub.Api.Services;

public interface IExecutionFence
{
    Task<IAsyncDisposable> ReadAsync(string sessionId, CancellationToken ct);
    Task<IAsyncDisposable> WriteAsync(string sessionId, CancellationToken ct);
}

public sealed class PostgresExecutionFence(IConfiguration configuration) : IExecutionFence
{
    private readonly NpgsqlDataSource _db = PostgresDataSources.Locks(configuration);
    public Task<IAsyncDisposable> ReadAsync(string sessionId, CancellationToken ct) => AcquireAsync(sessionId, true, ct);
    public Task<IAsyncDisposable> WriteAsync(string sessionId, CancellationToken ct) => AcquireAsync(sessionId, false, ct);
    private async Task<IAsyncDisposable> AcquireAsync(string sessionId, bool shared, CancellationToken ct)
    {
        var connection = await _db.OpenConnectionAsync(ct);
        try
        {
            // Key space 2 is separate from browser leases and the session lifecycle lock.
            await using var command = new NpgsqlCommand(shared
                ? "SELECT pg_advisory_lock_shared(hashtextextended(@id,2))"
                : "SELECT pg_advisory_lock(hashtextextended(@id,2))", connection);
            command.CommandTimeout = 30;
            command.Parameters.AddWithValue("id", sessionId);
            await command.ExecuteNonQueryAsync(ct);
            return new Lease(connection, sessionId, shared);
        }
        catch { await connection.DisposeAsync(); throw; }
    }
    private sealed class Lease(NpgsqlConnection connection, string id, bool shared) : IAsyncDisposable
    {
        private int _disposed;
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try
            {
                await using var command = new NpgsqlCommand(shared
                    ? "SELECT pg_advisory_unlock_shared(hashtextextended(@id,2))"
                    : "SELECT pg_advisory_unlock(hashtextextended(@id,2))", connection);
                command.Parameters.AddWithValue("id", id);
                await command.ExecuteNonQueryAsync();
            }
            finally { await connection.DisposeAsync(); }
        }
    }
}
