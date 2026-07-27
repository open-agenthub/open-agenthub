using Npgsql;

namespace AgentHub.Api.Browser;

public interface IBrowserSessionLock
{
    Task<IAsyncDisposable> AcquireAsync(string sessionId, CancellationToken ct = default);
}

public sealed class PostgresBrowserSessionLock : IBrowserSessionLock
{
    private readonly NpgsqlDataSource database;

    public PostgresBrowserSessionLock(IConfiguration configuration)
    {
        database = NpgsqlDataSource.Create(configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("ConnectionStrings:Postgres is missing."));
    }

    public async Task<IAsyncDisposable> AcquireAsync(string sessionId, CancellationToken ct = default)
    {
        var connection = await database.OpenConnectionAsync(ct);
        try
        {
            await using var command = new NpgsqlCommand(
                "SELECT pg_advisory_lock(hashtextextended(@session, 0))", connection);
            command.Parameters.AddWithValue("session", sessionId);
            await command.ExecuteNonQueryAsync(ct);
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
        private int disposed;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            try
            {
                await using var command = new NpgsqlCommand(
                    "SELECT pg_advisory_unlock(hashtextextended(@session, 0))", connection);
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
