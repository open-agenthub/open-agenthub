using Npgsql;

namespace AgentHub.Api.Persistence;

public sealed record ExecutionUsageSnapshot(long Generation, string Agent, string? CredentialId, string Billing,
    long InputTokens, long OutputTokens, long CacheReadTokens, long CacheCreationTokens, long ReasoningTokens,
    long Turns, double CostUsd, double ApiCostUsd, double EstimatedCostUsd);

public interface IExecutionUsageSnapshots
{
    Task InitializeAsync(CancellationToken ct = default);
    Task CaptureAsync(string owner, string id, long generation, CancellationToken ct = default);
    Task<IReadOnlyList<ExecutionUsageSnapshot>> ReadAsync(string owner, string id, CancellationToken ct = default);
}

public sealed class PostgresExecutionUsageSnapshots(IConfiguration configuration) : IExecutionUsageSnapshots
{
    private readonly NpgsqlDataSource _db = PostgresDataSources.Shared(configuration);
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await SchemaScript.ApplyAsync(_db, nameof(PostgresExecutionUsageSnapshots), """
            CREATE TABLE IF NOT EXISTS session_execution_usage (
                session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
                generation BIGINT NOT NULL, agent TEXT NOT NULL, credential_id TEXT, billing TEXT NOT NULL,
                input_tokens BIGINT NOT NULL, output_tokens BIGINT NOT NULL, cache_read_tokens BIGINT NOT NULL,
                cache_creation_tokens BIGINT NOT NULL, reasoning_tokens BIGINT NOT NULL, turns BIGINT NOT NULL,
                cost_usd DOUBLE PRECISION NOT NULL, api_cost_usd DOUBLE PRECISION NOT NULL, estimated_cost_usd DOUBLE PRECISION NOT NULL,
                PRIMARY KEY(session_id,generation)
            )
            """, ct);
    }
    public async Task CaptureAsync(string owner, string id, long generation, CancellationToken ct = default)
    {
        // Capture cumulative totals while the caller holds the exclusive execution fence.
        // Differencing adjacent checkpoints preserves the provider/account of each execution,
        // including legacy generation zero, without rewriting the existing monthly aggregates.
        await using var command = _db.CreateCommand("""
            INSERT INTO session_execution_usage
            SELECT s.id,@generation,s.agent,s.resolved_credential_id,COALESCE(s.usage_billing,u.billing,'unknown'),
                COALESCE(u.input_tokens,0),COALESCE(u.output_tokens,0),COALESCE(u.cache_read_tokens,0),
                COALESCE(u.cache_creation_tokens,0),COALESCE(u.reasoning_tokens,0),COALESCE(u.turns,0),
                COALESCE(u.cost_usd,0),COALESCE(u.api_cost_usd,0),COALESCE(u.estimated_cost_usd,0)
            FROM sessions s LEFT JOIN session_usage u ON u.session_id=s.id
            WHERE s.id=@id AND s.owner=@owner AND s.execution_generation=@generation
            ON CONFLICT(session_id,generation) DO UPDATE SET
                input_tokens=EXCLUDED.input_tokens,output_tokens=EXCLUDED.output_tokens,cache_read_tokens=EXCLUDED.cache_read_tokens,
                cache_creation_tokens=EXCLUDED.cache_creation_tokens,reasoning_tokens=EXCLUDED.reasoning_tokens,turns=EXCLUDED.turns,
                cost_usd=EXCLUDED.cost_usd,api_cost_usd=EXCLUDED.api_cost_usd,estimated_cost_usd=EXCLUDED.estimated_cost_usd
            """);
        command.Parameters.AddWithValue("id", id); command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("generation", generation);
        await command.ExecuteNonQueryAsync(ct);
    }
    public async Task<IReadOnlyList<ExecutionUsageSnapshot>> ReadAsync(string owner, string id, CancellationToken ct = default)
    {
        await using var command = _db.CreateCommand("""
            SELECT e.generation,e.agent,e.credential_id,e.billing,e.input_tokens,e.output_tokens,e.cache_read_tokens,
                e.cache_creation_tokens,e.reasoning_tokens,e.turns,e.cost_usd,e.api_cost_usd,e.estimated_cost_usd
            FROM session_execution_usage e JOIN sessions s ON s.id=e.session_id
            WHERE s.id=@id AND s.owner=@owner ORDER BY generation
            """);
        command.Parameters.AddWithValue("id", id); command.Parameters.AddWithValue("owner", owner);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<ExecutionUsageSnapshot>();
        ExecutionUsageSnapshot? previous = null;
        while (await reader.ReadAsync(ct))
        {
            var current = new ExecutionUsageSnapshot(reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3),
                reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7), reader.GetInt64(8), reader.GetInt64(9),
                reader.GetDouble(10), reader.GetDouble(11), reader.GetDouble(12));
            result.Add(current with
            {
                InputTokens = current.InputTokens - (previous?.InputTokens ?? 0), OutputTokens = current.OutputTokens - (previous?.OutputTokens ?? 0),
                CacheReadTokens = current.CacheReadTokens - (previous?.CacheReadTokens ?? 0), CacheCreationTokens = current.CacheCreationTokens - (previous?.CacheCreationTokens ?? 0),
                ReasoningTokens = current.ReasoningTokens - (previous?.ReasoningTokens ?? 0), Turns = current.Turns - (previous?.Turns ?? 0),
                CostUsd = current.CostUsd - (previous?.CostUsd ?? 0), ApiCostUsd = current.ApiCostUsd - (previous?.ApiCostUsd ?? 0),
                EstimatedCostUsd = current.EstimatedCostUsd - (previous?.EstimatedCostUsd ?? 0)
            });
            previous = current;
        }
        return result;
    }
}
