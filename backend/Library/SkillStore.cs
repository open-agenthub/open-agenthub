using AgentHub.Api.Storage;
using Npgsql;

namespace AgentHub.Api.Library;

public interface ISkillStore
{
    Task InitializeAsync(CancellationToken ct = default);
    Task<SkillRecord> CreateAsync(string owner, SaveSkillRequest request, CancellationToken ct = default);
    Task<SkillRecord> UpdateAsync(string owner, string id, SaveSkillRequest request, CancellationToken ct = default);
    Task DeleteAsync(string owner, string id, CancellationToken ct = default);
    Task<IReadOnlyList<SkillRecord>> ListByOwnerAsync(string owner, CancellationToken ct = default);
    /// <summary>Fetches records by id regardless of owner — access is the caller's concern.</summary>
    Task<IReadOnlyList<SkillRecord>> GetManyAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default);
    /// <summary>Resolves the SKILL.md content from S3 or the fallback column.</summary>
    Task<string?> GetContentAsync(SkillRecord record, CancellationToken ct = default);
}

/// <summary>
/// Postgres persistence for the personal skill library. The SKILL.md content
/// is written to S3 when object storage is configured; otherwise it falls back
/// to the content column, so the feature also works without S3.
/// </summary>
public sealed class SkillStore : ISkillStore
{
    private readonly NpgsqlDataSource _db;
    private readonly IArtifactStore _artifacts;
    private readonly ILogger<SkillStore> _log;

    public SkillStore(IConfiguration cfg, IArtifactStore artifacts, ILogger<SkillStore> log)
    {
        var cs = cfg.GetConnectionString("Postgres")
                 ?? throw new InvalidOperationException("ConnectionStrings:Postgres is missing.");
        _db = NpgsqlDataSource.Create(cs);
        _artifacts = artifacts;
        _log = log;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        const string ddl = """
            CREATE TABLE IF NOT EXISTS skills (
                id            TEXT PRIMARY KEY,
                owner         TEXT NOT NULL,
                name          TEXT NOT NULL,
                description   TEXT NOT NULL DEFAULT '',
                content       TEXT,
                content_in_s3 BOOLEAN NOT NULL DEFAULT FALSE,
                created_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
                updated_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
                UNIQUE (owner, name)
            );
            CREATE INDEX IF NOT EXISTS idx_skills_owner ON skills(owner);
            """;
        await using var cmd = _db.CreateCommand(ddl);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<SkillRecord> CreateAsync(
        string owner, SaveSkillRequest request, CancellationToken ct = default)
    {
        var id = Guid.NewGuid().ToString("n")[..12];
        var name = LibraryValidation.ValidateSkillName(request.Name);
        var description = LibraryValidation.ValidateDescription(request.Description);
        var content = LibraryValidation.ValidateSkillContent(request.Content);
        var inS3 = await _artifacts.TryPutTextAsync(IArtifactStore.SkillKey(id), content, ct);

        const string sql = """
            INSERT INTO skills (id, owner, name, description, content, content_in_s3)
            VALUES (@id, @owner, @name, @description, @content, @inS3)
            RETURNING created_at, updated_at
            """;
        await using var cmd = _db.CreateCommand(sql);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("owner", owner);
        cmd.Parameters.AddWithValue("name", name);
        cmd.Parameters.AddWithValue("description", description);
        cmd.Parameters.AddWithValue("content", inS3 ? DBNull.Value : content);
        cmd.Parameters.AddWithValue("inS3", inS3);
        try
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            return new SkillRecord
            {
                Id = id, Owner = owner, Name = name, Description = description, ContentInS3 = inS3,
                CreatedAt = reader.GetDateTime(0), UpdatedAt = reader.GetDateTime(1)
            };
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            if (inS3) await _artifacts.DeleteAsync(IArtifactStore.SkillKey(id), CancellationToken.None);
            throw new ArgumentException("You already have a skill with this name.");
        }
    }

    public async Task<SkillRecord> UpdateAsync(
        string owner, string id, SaveSkillRequest request, CancellationToken ct = default)
    {
        var name = LibraryValidation.ValidateSkillName(request.Name);
        var description = LibraryValidation.ValidateDescription(request.Description);
        var content = LibraryValidation.ValidateSkillContent(request.Content);
        var inS3 = await _artifacts.TryPutTextAsync(IArtifactStore.SkillKey(id), content, ct);

        const string sql = """
            UPDATE skills
            SET name = @name, description = @description,
                content = @content, content_in_s3 = @inS3, updated_at = now()
            WHERE id = @id AND owner = @owner
            RETURNING created_at, updated_at
            """;
        await using var cmd = _db.CreateCommand(sql);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("owner", owner);
        cmd.Parameters.AddWithValue("name", name);
        cmd.Parameters.AddWithValue("description", description);
        cmd.Parameters.AddWithValue("content", inS3 ? DBNull.Value : content);
        cmd.Parameters.AddWithValue("inS3", inS3);
        try
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                throw new KeyNotFoundException();
            return new SkillRecord
            {
                Id = id, Owner = owner, Name = name, Description = description, ContentInS3 = inS3,
                CreatedAt = reader.GetDateTime(0), UpdatedAt = reader.GetDateTime(1)
            };
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new ArgumentException("You already have a skill with this name.");
        }
    }

    public async Task DeleteAsync(string owner, string id, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand(
            "DELETE FROM skills WHERE id = @id AND owner = @owner");
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("owner", owner);
        if (await cmd.ExecuteNonQueryAsync(ct) == 0)
            throw new KeyNotFoundException();
        try
        {
            await _artifacts.DeleteAsync(IArtifactStore.SkillKey(id), ct);
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Could not delete S3 content of skill {Id}", id);
        }
    }

    public async Task<IReadOnlyList<SkillRecord>> ListByOwnerAsync(
        string owner, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand($"{SelectBase} WHERE owner = @owner ORDER BY name");
        cmd.Parameters.AddWithValue("owner", owner);
        return await QueryAsync(cmd, ct);
    }

    /// <summary>Fetches records by id regardless of owner — access is the caller's concern.</summary>
    public async Task<IReadOnlyList<SkillRecord>> GetManyAsync(
        IReadOnlyCollection<string> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return [];
        await using var cmd = _db.CreateCommand($"{SelectBase} WHERE id = ANY(@ids) ORDER BY name");
        cmd.Parameters.AddWithValue("ids", ids.ToArray());
        return await QueryAsync(cmd, ct);
    }

    /// <summary>Resolves the SKILL.md content from S3 or the fallback column.</summary>
    public async Task<string?> GetContentAsync(SkillRecord record, CancellationToken ct = default)
    {
        if (record.ContentInS3)
            return await _artifacts.GetTextAsync(IArtifactStore.SkillKey(record.Id), ct);

        await using var cmd = _db.CreateCommand("SELECT content FROM skills WHERE id = @id");
        cmd.Parameters.AddWithValue("id", record.Id);
        var value = await cmd.ExecuteScalarAsync(ct);
        return value is string s ? s : null;
    }

    private const string SelectBase =
        "SELECT id, owner, name, description, content_in_s3, created_at, updated_at FROM skills";

    private static async Task<IReadOnlyList<SkillRecord>> QueryAsync(
        NpgsqlCommand cmd, CancellationToken ct)
    {
        var list = new List<SkillRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new SkillRecord
            {
                Id = reader.GetString(0),
                Owner = reader.GetString(1),
                Name = reader.GetString(2),
                Description = reader.GetString(3),
                ContentInS3 = reader.GetBoolean(4),
                CreatedAt = reader.GetDateTime(5),
                UpdatedAt = reader.GetDateTime(6)
            });
        }
        return list;
    }
}
