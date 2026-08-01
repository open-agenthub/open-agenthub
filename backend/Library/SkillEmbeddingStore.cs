using Npgsql;

namespace AgentHub.Api.Library;

public interface ISkillEmbeddingStore
{
    Task InitializeAsync(CancellationToken ct = default);
    Task UpsertAsync(string skillId, string model, float[] vector, CancellationToken ct = default);
    Task DeleteAsync(string skillId, CancellationToken ct = default);
    /// <summary>Vectors for the given skills; entries with a different model are skipped.</summary>
    Task<IReadOnlyDictionary<string, float[]>> GetManyAsync(
        IReadOnlyCollection<string> skillIds, string model, CancellationToken ct = default);
}

/// <summary>
/// Skill embeddings in plain Postgres (REAL[] column, cosine computed in-process).
/// Skill libraries are small, so a scan over the accessible set beats requiring
/// the pgvector extension on every deployment.
/// </summary>
public sealed class SkillEmbeddingStore : ISkillEmbeddingStore
{
    private readonly NpgsqlDataSource _db;

    public SkillEmbeddingStore(IConfiguration cfg)
    {
        var cs = cfg.GetConnectionString("Postgres")
                 ?? throw new InvalidOperationException("ConnectionStrings:Postgres is missing.");
        _db = NpgsqlDataSource.Create(cs);
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        const string ddl = """
            CREATE TABLE IF NOT EXISTS skill_embeddings (
                skill_id   TEXT PRIMARY KEY,
                model      TEXT NOT NULL,
                vector     REAL[] NOT NULL,
                updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
            );
            """;
        await using var cmd = _db.CreateCommand(ddl);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpsertAsync(
        string skillId, string model, float[] vector, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO skill_embeddings (skill_id, model, vector, updated_at)
            VALUES (@id, @model, @vector, now())
            ON CONFLICT (skill_id) DO UPDATE
                SET model = EXCLUDED.model, vector = EXCLUDED.vector, updated_at = now()
            """;
        await using var cmd = _db.CreateCommand(sql);
        cmd.Parameters.AddWithValue("id", skillId);
        cmd.Parameters.AddWithValue("model", model);
        cmd.Parameters.AddWithValue("vector", vector);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteAsync(string skillId, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand("DELETE FROM skill_embeddings WHERE skill_id = @id");
        cmd.Parameters.AddWithValue("id", skillId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyDictionary<string, float[]>> GetManyAsync(
        IReadOnlyCollection<string> skillIds, string model, CancellationToken ct = default)
    {
        if (skillIds.Count == 0) return new Dictionary<string, float[]>();
        const string sql = "SELECT skill_id, vector FROM skill_embeddings WHERE skill_id = ANY(@ids) AND model = @model";
        await using var cmd = _db.CreateCommand(sql);
        cmd.Parameters.AddWithValue("ids", skillIds.ToArray());
        cmd.Parameters.AddWithValue("model", model);
        var result = new Dictionary<string, float[]>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result[reader.GetString(0)] = reader.GetFieldValue<float[]>(1);
        return result;
    }
}
