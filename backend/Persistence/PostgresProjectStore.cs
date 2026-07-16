using AgentHub.Api.Models;
using Npgsql;

namespace AgentHub.Api.Persistence;

public interface IProjectStore
{
    Task InitializeAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ProjectInfo>> ListAsync(string owner, CancellationToken ct = default);
    Task<ProjectInfo?> GetAsync(string owner, string id, CancellationToken ct = default);
    Task<ProjectInfo> CreateAsync(string owner, CreateProjectRequest request, CancellationToken ct = default);
    Task<ProjectInfo?> UpdateAsync(string owner, string id, UpdateProjectRequest request, CancellationToken ct = default);
    Task<bool> DeleteAsync(string owner, string id, CancellationToken ct = default);
}

public sealed class PostgresProjectStore : IProjectStore
{
    private readonly NpgsqlDataSource _db;

    public PostgresProjectStore(IConfiguration cfg)
    {
        var cs = cfg.GetConnectionString("Postgres")
                 ?? throw new InvalidOperationException("ConnectionStrings:Postgres is missing.");
        _db = NpgsqlDataSource.Create(cs);
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        const string ddl = """
            CREATE TABLE IF NOT EXISTS projects (
                id TEXT PRIMARY KEY,
                owner TEXT NOT NULL,
                name TEXT NOT NULL,
                color TEXT,
                sort_order INTEGER NOT NULL DEFAULT 0,
                created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
                updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
            );
            CREATE INDEX IF NOT EXISTS idx_projects_owner ON projects(owner);
            CREATE UNIQUE INDEX IF NOT EXISTS uq_projects_owner_name ON projects(owner, lower(name));
            """;
        await using var cmd = _db.CreateCommand(ddl);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<ProjectInfo>> ListAsync(string owner, CancellationToken ct = default)
    {
        var projects = new List<ProjectInfo>();
        await using var cmd = _db.CreateCommand("""
            SELECT id, name, color, sort_order FROM projects
            WHERE owner = @owner ORDER BY sort_order, name, id
            """);
        cmd.Parameters.AddWithValue("owner", owner);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) projects.Add(Map(reader));
        return projects;
    }

    public async Task<ProjectInfo?> GetAsync(string owner, string id, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand("""
            SELECT id, name, color, sort_order FROM projects WHERE owner = @owner AND id = @id
            """);
        cmd.Parameters.AddWithValue("owner", owner);
        cmd.Parameters.AddWithValue("id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Map(reader) : null;
    }

    public async Task<ProjectInfo> CreateAsync(string owner, CreateProjectRequest request, CancellationToken ct = default)
    {
        var id = Guid.NewGuid().ToString("n")[..12];
        var name = request.Name.Trim();
        await using var cmd = _db.CreateCommand("""
            INSERT INTO projects (id, owner, name, color, sort_order)
            VALUES (@id, @owner, @name, @color,
                (SELECT COALESCE(MAX(sort_order), -1) + 1 FROM projects WHERE owner = @owner))
            RETURNING id, name, color, sort_order
            """);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("owner", owner);
        cmd.Parameters.AddWithValue("name", name);
        cmd.Parameters.AddWithValue("color", (object?)request.Color ?? DBNull.Value);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return Map(reader);
    }

    public async Task<ProjectInfo?> UpdateAsync(string owner, string id, UpdateProjectRequest request, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand("""
            UPDATE projects SET
                name = COALESCE(@name, name),
                color = COALESCE(@color, color),
                sort_order = COALESCE(@sortOrder, sort_order),
                updated_at = now()
            WHERE owner = @owner AND id = @id
            RETURNING id, name, color, sort_order
            """);
        cmd.Parameters.AddWithValue("owner", owner);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("name", (object?)(request.Name?.Trim()) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("color", (object?)request.Color ?? DBNull.Value);
        cmd.Parameters.AddWithValue("sortOrder", (object?)request.SortOrder ?? DBNull.Value);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Map(reader) : null;
    }

    public async Task<bool> DeleteAsync(string owner, string id, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var clear = new NpgsqlCommand("""
            UPDATE sessions SET project_id = NULL, updated_at = now()
            WHERE owner = @owner AND project_id = @id
            """, connection, transaction))
        {
            clear.Parameters.AddWithValue("owner", owner);
            clear.Parameters.AddWithValue("id", id);
            await clear.ExecuteNonQueryAsync(ct);
        }

        await using var delete = new NpgsqlCommand("DELETE FROM projects WHERE owner = @owner AND id = @id", connection, transaction);
        delete.Parameters.AddWithValue("owner", owner);
        delete.Parameters.AddWithValue("id", id);
        var deleted = await delete.ExecuteNonQueryAsync(ct) == 1;
        await transaction.CommitAsync(ct);
        return deleted;
    }

    private static ProjectInfo Map(NpgsqlDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.IsDBNull(2) ? null : reader.GetString(2),
        reader.GetInt32(3));
}
