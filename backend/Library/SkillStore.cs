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

    /// <summary>Version history, newest first. Access is the caller's concern.</summary>
    Task<IReadOnlyList<SkillVersionRecord>> ListVersionsAsync(string id, CancellationToken ct = default);
    /// <summary>SKILL.md content of one specific version; null when the version is unknown.</summary>
    Task<string?> GetVersionContentAsync(string id, int version, CancellationToken ct = default);
    /// <summary>Re-publishes an old version's content as a new head version.</summary>
    Task<SkillRecord> RestoreVersionAsync(string owner, string id, int version, string? savedBy = null,
        CancellationToken ct = default);

    /// <summary>Full-text search restricted to the given accessible skill ids.
    /// Returns (record, rank) pairs ordered by rank descending.</summary>
    Task<IReadOnlyList<(SkillRecord Record, double Rank)>> SearchAsync(
        IReadOnlyCollection<string> ids, string query, int limit, CancellationToken ct = default);
}

/// <summary>
/// Postgres persistence for the skill library. The SKILL.md content is written
/// to S3 when object storage is configured; otherwise it falls back to the
/// content column, so the feature also works without S3. Every save writes an
/// immutable row into skill_versions (content archived per version), and a
/// capped copy of the head content is kept in search_text so full-text search
/// works regardless of where the content itself lives.
/// </summary>
public sealed class SkillStore : ISkillStore
{
    /// <summary>Head content is capped to this many chars for the FTS copy.</summary>
    public const int SearchTextChars = 8_000;

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
            ALTER TABLE skills ADD COLUMN IF NOT EXISTS project_id TEXT;
            ALTER TABLE skills ADD COLUMN IF NOT EXISTS version INT NOT NULL DEFAULT 1;
            ALTER TABLE skills ADD COLUMN IF NOT EXISTS search_text TEXT NOT NULL DEFAULT '';
            -- Uniqueness is per scope: the same name may exist personally and per project.
            ALTER TABLE skills DROP CONSTRAINT IF EXISTS skills_owner_name_key;
            CREATE UNIQUE INDEX IF NOT EXISTS idx_skills_owner_scope_name
                ON skills(owner, COALESCE(project_id, ''), name);
            CREATE INDEX IF NOT EXISTS idx_skills_fts ON skills
                USING GIN (to_tsvector('simple', name || ' ' || description || ' ' || search_text));
            -- Rows created before search_text existed: backfill from the fallback column.
            UPDATE skills SET search_text = left(content, 8000)
                WHERE search_text = '' AND content IS NOT NULL;

            CREATE TABLE IF NOT EXISTS skill_versions (
                skill_id      TEXT NOT NULL,
                version       INT NOT NULL,
                name          TEXT NOT NULL,
                description   TEXT NOT NULL DEFAULT '',
                content       TEXT,
                content_in_s3 BOOLEAN NOT NULL DEFAULT FALSE,
                created_by    TEXT NOT NULL DEFAULT '',
                comment       TEXT NOT NULL DEFAULT '',
                created_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
                PRIMARY KEY (skill_id, version)
            );
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
        var comment = LibraryValidation.ValidateComment(request.Comment);
        var inS3 = await PutContentAsync(id, 1, content, ct);

        // One command = one implicit transaction: a failure in either INSERT rolls both back.
        const string sql = """
            INSERT INTO skills (id, owner, name, description, content, content_in_s3, project_id, version, search_text)
            VALUES (@id, @owner, @name, @description, @content, @inS3, @project, 1, @searchText)
            RETURNING created_at, updated_at;
            INSERT INTO skill_versions (skill_id, version, name, description, content, content_in_s3, created_by, comment)
            VALUES (@id, 1, @name, @description, @content, @inS3, @savedBy, @comment)
            """;
        await using var cmd = _db.CreateCommand(sql);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("owner", owner);
        cmd.Parameters.AddWithValue("name", name);
        cmd.Parameters.AddWithValue("description", description);
        cmd.Parameters.AddWithValue("content", inS3 ? DBNull.Value : content);
        cmd.Parameters.AddWithValue("inS3", inS3);
        cmd.Parameters.AddWithValue("project", (object?)request.ProjectId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("searchText", content[..Math.Min(content.Length, SearchTextChars)]);
        cmd.Parameters.AddWithValue("savedBy", request.SavedBy ?? owner);
        cmd.Parameters.AddWithValue("comment", comment);
        try
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            return new SkillRecord
            {
                Id = id, Owner = owner, Name = name, Description = description, ContentInS3 = inS3,
                ProjectId = request.ProjectId, Version = 1,
                CreatedAt = reader.GetDateTime(0), UpdatedAt = reader.GetDateTime(1)
            };
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            if (inS3) await DeleteContentAsync(id, [1], CancellationToken.None);
            throw new ArgumentException("A skill with this name already exists in this scope.");
        }
    }

    public async Task<SkillRecord> UpdateAsync(
        string owner, string id, SaveSkillRequest request, CancellationToken ct = default)
    {
        var name = LibraryValidation.ValidateSkillName(request.Name);
        var description = LibraryValidation.ValidateDescription(request.Description);
        var content = LibraryValidation.ValidateSkillContent(request.Content);
        var comment = LibraryValidation.ValidateComment(request.Comment);
        return await SaveNewVersionAsync(owner, id, name, description, content,
            request.SavedBy ?? owner, comment, ct);
    }

    public async Task<SkillRecord> RestoreVersionAsync(
        string owner, string id, int version, string? savedBy = null, CancellationToken ct = default)
    {
        var versions = await ListVersionsAsync(id, ct);
        var target = versions.FirstOrDefault(v => v.Version == version)
            ?? throw new KeyNotFoundException();
        var content = await GetVersionContentAsync(id, version, ct)
            ?? throw new KeyNotFoundException();
        return await SaveNewVersionAsync(owner, id, target.Name, target.Description, content,
            savedBy ?? owner, $"Restored version {version}.", ct);
    }

    /// <summary>Bumps the head to a fresh version and archives it in skill_versions.
    /// The head's version counter is the single source of the next number.</summary>
    private async Task<SkillRecord> SaveNewVersionAsync(
        string owner, string id, string name, string description, string content,
        string savedBy, string comment, CancellationToken ct)
    {
        var current = (await GetManyAsync([id], ct)).FirstOrDefault(r => r.Owner == owner)
            ?? throw new KeyNotFoundException();
        var next = current.Version + 1;
        var inS3 = await PutContentAsync(id, next, content, ct);

        // One command = one implicit transaction; the version row is only written
        // when the optimistic head update actually matched (owner + expected version).
        const string sql = """
            UPDATE skills
            SET name = @name, description = @description, content = @content,
                content_in_s3 = @inS3, version = @version, search_text = @searchText, updated_at = now()
            WHERE id = @id AND owner = @owner AND version = @prevVersion
            RETURNING project_id, created_at, updated_at;
            INSERT INTO skill_versions (skill_id, version, name, description, content, content_in_s3, created_by, comment)
            SELECT @id, @version, @name, @description, @content, @inS3, @savedBy, @comment
            WHERE EXISTS (SELECT 1 FROM skills WHERE id = @id AND owner = @owner AND version = @version)
            """;
        await using var cmd = _db.CreateCommand(sql);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("owner", owner);
        cmd.Parameters.AddWithValue("name", name);
        cmd.Parameters.AddWithValue("description", description);
        cmd.Parameters.AddWithValue("content", inS3 ? DBNull.Value : content);
        cmd.Parameters.AddWithValue("inS3", inS3);
        cmd.Parameters.AddWithValue("version", next);
        cmd.Parameters.AddWithValue("prevVersion", current.Version);
        cmd.Parameters.AddWithValue("searchText", content[..Math.Min(content.Length, SearchTextChars)]);
        cmd.Parameters.AddWithValue("savedBy", savedBy);
        cmd.Parameters.AddWithValue("comment", comment);
        try
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                // Row gone or the version moved under us (concurrent save).
                if ((await GetManyAsync([id], ct)).All(r => r.Owner != owner))
                    throw new KeyNotFoundException();
                throw new ArgumentException("The skill changed concurrently — retry the save.");
            }
            return new SkillRecord
            {
                Id = id, Owner = owner, Name = name, Description = description, ContentInS3 = inS3,
                ProjectId = reader.IsDBNull(0) ? null : reader.GetString(0),
                Version = next, CreatedAt = reader.GetDateTime(1), UpdatedAt = reader.GetDateTime(2)
            };
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new ArgumentException("A skill with this name already exists in this scope.");
        }
    }

    public async Task DeleteAsync(string owner, string id, CancellationToken ct = default)
    {
        var versions = (await ListVersionsAsync(id, ct)).Select(v => v.Version).ToList();
        const string sql = """
            DELETE FROM skill_versions WHERE skill_id = @id
                AND EXISTS (SELECT 1 FROM skills WHERE id = @id AND owner = @owner);
            DELETE FROM skills WHERE id = @id AND owner = @owner
            """;
        await using var cmd = _db.CreateCommand(sql);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("owner", owner);
        if (await cmd.ExecuteNonQueryAsync(ct) == 0)
            throw new KeyNotFoundException();
        try
        {
            await DeleteContentAsync(id, versions, ct);
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
            return await _artifacts.GetTextAsync(IArtifactStore.SkillVersionKey(record.Id, record.Version), ct)
                ?? await _artifacts.GetTextAsync(IArtifactStore.SkillKey(record.Id), ct);

        await using var cmd = _db.CreateCommand("SELECT content FROM skills WHERE id = @id");
        cmd.Parameters.AddWithValue("id", record.Id);
        var value = await cmd.ExecuteScalarAsync(ct);
        return value is string s ? s : null;
    }

    public async Task<IReadOnlyList<SkillVersionRecord>> ListVersionsAsync(
        string id, CancellationToken ct = default)
    {
        const string sql = """
            SELECT version, name, description, content_in_s3, created_by, comment, created_at
            FROM skill_versions WHERE skill_id = @id ORDER BY version DESC
            """;
        await using var cmd = _db.CreateCommand(sql);
        cmd.Parameters.AddWithValue("id", id);
        var list = new List<SkillVersionRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new SkillVersionRecord
            {
                SkillId = id,
                Version = reader.GetInt32(0),
                Name = reader.GetString(1),
                Description = reader.GetString(2),
                ContentInS3 = reader.GetBoolean(3),
                CreatedBy = reader.GetString(4),
                Comment = reader.GetString(5),
                CreatedAt = reader.GetDateTime(6)
            });
        }
        return list;
    }

    public async Task<string?> GetVersionContentAsync(
        string id, int version, CancellationToken ct = default)
    {
        const string sql = "SELECT content, content_in_s3 FROM skill_versions WHERE skill_id = @id AND version = @version";
        await using var cmd = _db.CreateCommand(sql);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("version", version);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        if (!reader.GetBoolean(1))
            return reader.IsDBNull(0) ? null : reader.GetString(0);
        return await _artifacts.GetTextAsync(IArtifactStore.SkillVersionKey(id, version), ct);
    }

    public async Task<IReadOnlyList<(SkillRecord Record, double Rank)>> SearchAsync(
        IReadOnlyCollection<string> ids, string query, int limit, CancellationToken ct = default)
    {
        if (ids.Count == 0 || string.IsNullOrWhiteSpace(query)) return [];
        // FTS over name/description/search_text plus a substring fallback, so short
        // fragments ("kube") still match even without a full lexeme.
        const string sql = """
            SELECT id, owner, name, description, content_in_s3, project_id, version, created_at, updated_at,
                   ts_rank(to_tsvector('simple', name || ' ' || description || ' ' || search_text),
                           plainto_tsquery('simple', @q)) AS rank
            FROM skills
            WHERE id = ANY(@ids)
              AND (to_tsvector('simple', name || ' ' || description || ' ' || search_text)
                       @@ plainto_tsquery('simple', @q)
                   OR name ILIKE @like OR description ILIKE @like)
            ORDER BY rank DESC, updated_at DESC
            LIMIT @limit
            """;
        await using var cmd = _db.CreateCommand(sql);
        cmd.Parameters.AddWithValue("ids", ids.ToArray());
        cmd.Parameters.AddWithValue("q", query);
        cmd.Parameters.AddWithValue("like", $"%{EscapeLike(query)}%");
        cmd.Parameters.AddWithValue("limit", limit);
        var list = new List<(SkillRecord, double)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add((MapRecord(reader), reader.GetDouble(9)));
        }
        return list;
    }

    private static string EscapeLike(string value) =>
        value.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");

    /// <summary>Writes head + versioned copy; false when no object storage is configured.</summary>
    private async Task<bool> PutContentAsync(string id, int version, string content, CancellationToken ct)
    {
        var inS3 = await _artifacts.TryPutTextAsync(IArtifactStore.SkillVersionKey(id, version), content, ct);
        if (inS3)
            await _artifacts.TryPutTextAsync(IArtifactStore.SkillKey(id), content, ct);
        return inS3;
    }

    private async Task DeleteContentAsync(string id, IEnumerable<int> versions, CancellationToken ct)
    {
        await _artifacts.DeleteAsync(IArtifactStore.SkillKey(id), ct);
        foreach (var version in versions)
            await _artifacts.DeleteAsync(IArtifactStore.SkillVersionKey(id, version), ct);
    }

    private const string SelectBase =
        "SELECT id, owner, name, description, content_in_s3, project_id, version, created_at, updated_at FROM skills";

    private static SkillRecord MapRecord(NpgsqlDataReader reader) => new()
    {
        Id = reader.GetString(0),
        Owner = reader.GetString(1),
        Name = reader.GetString(2),
        Description = reader.GetString(3),
        ContentInS3 = reader.GetBoolean(4),
        ProjectId = reader.IsDBNull(5) ? null : reader.GetString(5),
        Version = reader.GetInt32(6),
        CreatedAt = reader.GetDateTime(7),
        UpdatedAt = reader.GetDateTime(8)
    };

    private static async Task<IReadOnlyList<SkillRecord>> QueryAsync(
        NpgsqlCommand cmd, CancellationToken ct)
    {
        var list = new List<SkillRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(MapRecord(reader));
        return list;
    }
}
