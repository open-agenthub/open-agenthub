using Npgsql;

namespace AgentHub.Api.Persistence;

public sealed class PostgresSessionOrderStore(IConfiguration configuration)
{
    private readonly NpgsqlDataSource _db = PostgresDataSources.Shared(configuration);

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var command = _db.CreateCommand("""
            CREATE TABLE IF NOT EXISTS session_sidebar_order (
                owner TEXT NOT NULL,
                session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
                position INTEGER NOT NULL,
                PRIMARY KEY (owner, session_id)
            );
            """);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<string>> GetAsync(string owner, CancellationToken ct = default)
    {
        await using var command = _db.CreateCommand("""
            SELECT o.session_id FROM session_sidebar_order o JOIN sessions s ON s.id = o.session_id
            WHERE o.owner = @owner AND s.owner = @owner ORDER BY o.position, o.session_id
            """);
        command.Parameters.AddWithValue("owner", owner);
        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(reader.GetString(0));
        return result;
    }

    public async Task<bool> SetAsync(string owner, IReadOnlyList<string> ids, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        // Serialize tabs and replicas for this user so replacing an order never interleaves.
        await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@owner, 3))", connection, transaction);
        command.Parameters.AddWithValue("owner", owner);
        await command.ExecuteNonQueryAsync(ct);
        command.CommandText = "DELETE FROM session_sidebar_order WHERE owner = @owner";
        await command.ExecuteNonQueryAsync(ct);
        command.CommandText = """
            INSERT INTO session_sidebar_order (owner, session_id, position)
            SELECT @owner, s.id, requested.position::integer
            FROM unnest(@ids::text[]) WITH ORDINALITY AS requested(id, position)
            JOIN sessions s ON s.id = requested.id AND s.owner = @owner
            """;
        command.Parameters.AddWithValue("ids", ids.ToArray());
        if (await command.ExecuteNonQueryAsync(ct) != ids.Count) return false;
        await transaction.CommitAsync(ct);
        return true;
    }
}
