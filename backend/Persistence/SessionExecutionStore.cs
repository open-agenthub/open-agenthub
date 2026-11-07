using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using AgentHub.Api.Models;
using AgentHub.Api.Services;
using AgentHub.Api.Storage;
using Npgsql;
using NpgsqlTypes;

namespace AgentHub.Api.Persistence;

public sealed record SessionExecution(long Generation, AgentKind Agent, string NativeId,
    string? CredentialId, string TranscriptKey, string StateKey, bool Sealed);
public sealed record ExecutionHistory(SessionExecution Current, IReadOnlyList<TranscriptEntry> Previous, bool Omitted, string CurrentTranscript);

public interface ISessionExecutionStore
{
    Task InitializeAsync(CancellationToken ct = default);
    Task<ExecutionHistory?> ReadAsync(string owner, string id, CancellationToken ct = default);
    Task<SessionExecution?> EnsureLegacyAsync(string owner, string id, CancellationToken ct = default);
    // Only lifecycle orchestration may seal and advance; there is deliberately no HTTP writer.
    Task SaveTranscriptAsync(string owner, string id, long generation, string jsonl, CancellationToken ct = default);
    Task AdvanceAsync(string owner, string id, long expectedGeneration, AgentKind agent,
        string nativeId, string? credentialId, IReadOnlyList<TranscriptEntry> finalEntries,
        CancellationToken ct = default);
}

public sealed class PostgresSessionExecutionStore(IConfiguration configuration) : ISessionExecutionStore
{
    public const int MaxHistoryEntries = 5000;
    public const int MaxHistoryChars = 1_000_000;
    private readonly NpgsqlDataSource _db = PostgresDataSources.Shared(configuration);

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var command = _db.CreateCommand("""
            CREATE TABLE IF NOT EXISTS session_executions (
                session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
                generation BIGINT NOT NULL CHECK (generation >= 0),
                agent TEXT NOT NULL, native_id TEXT NOT NULL, credential_id TEXT,
                transcript_key TEXT NOT NULL, state_key TEXT NOT NULL,
                current_transcript TEXT NOT NULL DEFAULT '',
                sealed BOOLEAN NOT NULL DEFAULT FALSE,
                PRIMARY KEY (session_id, generation)
            );
            CREATE UNIQUE INDEX IF NOT EXISTS session_execution_current
                ON session_executions(session_id) WHERE NOT sealed;
            CREATE TABLE IF NOT EXISTS session_execution_entries (
                session_id TEXT NOT NULL, generation BIGINT NOT NULL, ordinal INTEGER NOT NULL,
                entry JSONB NOT NULL,
                PRIMARY KEY(session_id, generation, ordinal),
                FOREIGN KEY(session_id, generation) REFERENCES session_executions(session_id, generation) ON DELETE CASCADE
            );
            """);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<SessionExecution?> EnsureLegacyAsync(string owner, string id, CancellationToken ct = default)
    {
        // Read adaptation preserves existing upload URLs. A missing legacy native ID must not
        // prevent reading its transcript while an older schema is being upgraded.
        await using var command = _db.CreateCommand("""
            INSERT INTO session_executions(session_id, generation, agent, native_id, credential_id, transcript_key, state_key)
            SELECT id, 0, agent, COALESCE(agent_session_id, ''), resolved_credential_id,
                @prefix || '/transcript.jsonl', @prefix || '/' || lower(agent) || '-state.tgz'
            FROM sessions WHERE id=@id AND owner=@owner
            ON CONFLICT DO NOTHING;
            """);
        command.Parameters.AddWithValue("prefix", $"sessions/{Sanitize(owner)}/{id}");
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("owner", owner);
        await command.ExecuteNonQueryAsync(ct);
        return (await ReadAsync(owner, id, ct))?.Current;
    }

    public async Task<ExecutionHistory?> ReadAsync(string owner, string id, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct);
        // One snapshot prevents an advance between reading the current generation and its prefix.
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        await using var current = new NpgsqlCommand("""
            SELECT e.generation,e.agent,e.native_id,e.credential_id,e.transcript_key,e.state_key,e.sealed,e.current_transcript
            FROM session_executions e JOIN sessions s ON s.id=e.session_id
            WHERE s.owner=@owner AND s.id=@id AND NOT e.sealed
            """, connection, transaction);
        current.Parameters.AddWithValue("owner", owner); current.Parameters.AddWithValue("id", id);
        SessionExecution execution;
        string snapshot;
        await using (var reader = await current.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) return null;
            execution = new(reader.GetInt64(0), Enum.Parse<AgentKind>(reader.GetString(1)), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetBoolean(6));
            snapshot = reader.GetString(7);
        }
        if (execution.Generation == 0)
        {
            await transaction.CommitAsync(ct);
            return new(execution, [], false, snapshot);
        }
        await using var entries = new NpgsqlCommand("""
            SELECT entry::text FROM session_execution_entries WHERE session_id=@id AND generation < @generation
            ORDER BY generation DESC, ordinal DESC LIMIT @limit
            """, connection, transaction);
        entries.Parameters.AddWithValue("id", id); entries.Parameters.AddWithValue("generation", execution.Generation);
        entries.Parameters.AddWithValue("limit", MaxHistoryEntries + 1);
        var result = new List<TranscriptEntry>(); var chars = 0; var omitted = false;
        await using (var reader = await entries.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var json = reader.GetString(0);
                if (result.Count == MaxHistoryEntries || chars + json.Length > MaxHistoryChars) { omitted = true; break; }
                chars += json.Length;
                result.Add(JsonSerializer.Deserialize<TranscriptEntry>(json)!);
            }
        }
        result.Reverse();
        await transaction.CommitAsync(ct);
        return new(execution, result, omitted, snapshot);
    }

    public async Task SaveTranscriptAsync(string owner, string id, long generation, string jsonl, CancellationToken ct = default)
    {
        if (jsonl.Length > MaxHistoryChars) throw new ArgumentException("Transcript exceeds snapshot budget.");
        await using var command = _db.CreateCommand("""
            UPDATE session_executions e SET current_transcript=@text FROM sessions s
            WHERE e.session_id=s.id AND s.owner=@owner AND s.id=@id AND e.generation=@generation AND NOT e.sealed
            """);
        command.Parameters.AddWithValue("text", jsonl); command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("id", id); command.Parameters.AddWithValue("generation", generation);
        if (await command.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("Execution is no longer current.");
    }

    public async Task AdvanceAsync(string owner, string id, long expectedGeneration, AgentKind agent,
        string nativeId, string? credentialId, IReadOnlyList<TranscriptEntry> finalEntries, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(agent) || string.IsNullOrWhiteSpace(nativeId) || nativeId.Length > 256)
            throw new ArgumentException("Invalid target execution.");
        if (expectedGeneration < 0 || expectedGeneration == long.MaxValue) throw new ArgumentException("Invalid generation.");
        var next = expectedGeneration + 1;
        var json = finalEntries.Select(e => JsonSerializer.Serialize(e with { Generation = expectedGeneration })).ToArray();
        if (json.Length > MaxHistoryEntries || json.Sum(e => (long)e.Length) > MaxHistoryChars)
            throw new ArgumentException("Seal exceeds retained history bounds; an explicit omission segment is required.");
        await using var connection = await _db.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        // Lock the session row so separate replicas cannot both seal the same execution.
        await using var seal = new NpgsqlCommand("""
            SELECT id FROM sessions WHERE id=@id AND owner=@owner FOR UPDATE;
            """, connection, transaction);
        seal.Parameters.AddWithValue("id", id); seal.Parameters.AddWithValue("owner", owner);
        if (await seal.ExecuteScalarAsync(ct) is null) throw new KeyNotFoundException();
        await using var update = new NpgsqlCommand("""
            UPDATE session_executions SET sealed=TRUE WHERE session_id=@id AND generation=@generation AND NOT sealed RETURNING agent
            """, connection, transaction);
        update.Parameters.AddWithValue("id", id); update.Parameters.AddWithValue("generation", expectedGeneration);
        var previousAgent = await update.ExecuteScalarAsync(ct) as string
            ?? throw new InvalidOperationException("Execution changed; reload before advancing.");
        if (expectedGeneration > 0)
        {
            await using var boundaryInsert = new NpgsqlCommand("INSERT INTO session_execution_entries VALUES (@id,@generation,-1,@entry)", connection, transaction);
            boundaryInsert.Parameters.AddWithValue("id", id); boundaryInsert.Parameters.AddWithValue("generation", expectedGeneration);
            boundaryInsert.Parameters.AddWithValue("entry", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(
                new TranscriptEntry("boundary", $"Provider execution: {previousAgent}", null, $"execution:{expectedGeneration}")
                { Generation = expectedGeneration, Agent = previousAgent }));
            await boundaryInsert.ExecuteNonQueryAsync(ct);
        }
        json = finalEntries.Select(e => JsonSerializer.Serialize(e with { Generation = expectedGeneration, Agent = previousAgent })).ToArray();
        if (json.Sum(e => (long)e.Length) > MaxHistoryChars) throw new ArgumentException("Seal exceeds retained history bounds.");
        for (var index = 0; index < json.Length; index++)
        {
            await using var insert = new NpgsqlCommand("""
                INSERT INTO session_execution_entries VALUES (@id,@generation,@ordinal,@entry)
                """, connection, transaction);
            insert.Parameters.AddWithValue("id", id); insert.Parameters.AddWithValue("generation", expectedGeneration);
            insert.Parameters.AddWithValue("ordinal", index); insert.Parameters.AddWithValue("entry", NpgsqlDbType.Jsonb, json[index]);
            await insert.ExecuteNonQueryAsync(ct);
        }
        await using var start = new NpgsqlCommand("""
            INSERT INTO session_executions(session_id,generation,agent,native_id,credential_id,transcript_key,state_key)
            VALUES (@id,@generation,@agent,@native,@credential,@transcript,@state);
            """, connection, transaction);
        start.Parameters.AddWithValue("id", id); start.Parameters.AddWithValue("generation", next);
        start.Parameters.AddWithValue("agent", agent.ToString()); start.Parameters.AddWithValue("native", nativeId);
        start.Parameters.AddWithValue("credential", (object?)credentialId ?? DBNull.Value);
        start.Parameters.AddWithValue("transcript", IArtifactStore.ExecutionTranscriptKey(Sanitize(owner), id, next));
        start.Parameters.AddWithValue("state", IArtifactStore.ExecutionStateKey(Sanitize(owner), id, next, agent));
        await start.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private static string Sanitize(string value) =>
        "u-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16].ToLowerInvariant();
}
