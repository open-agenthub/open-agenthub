using Npgsql;

namespace AgentHub.Api.Library;

public interface IMcpServerStore
{
    Task InitializeAsync(CancellationToken ct = default);
    Task<McpServerRecord> CreateAsync(string owner, SaveMcpServerRequest request, CancellationToken ct = default);
    Task<McpServerRecord> UpdateAsync(string owner, string id, SaveMcpServerRequest request, CancellationToken ct = default);
    Task DeleteAsync(string owner, string id, CancellationToken ct = default);
    Task<IReadOnlyList<McpServerRecord>> ListByOwnerAsync(string owner, CancellationToken ct = default);
    /// <summary>Fetches records by id regardless of owner — access is the caller's concern.</summary>
    Task<IReadOnlyList<McpServerRecord>> GetManyAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default);
}

/// <summary>Postgres persistence for the personal MCP server library.</summary>
public sealed class McpServerStore : IMcpServerStore
{
    private readonly NpgsqlDataSource _db;

    public McpServerStore(IConfiguration cfg)
    {
        var cs = cfg.GetConnectionString("Postgres")
                 ?? throw new InvalidOperationException("ConnectionStrings:Postgres is missing.");
        _db = NpgsqlDataSource.Create(cs);
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        const string ddl = """
            CREATE TABLE IF NOT EXISTS mcp_servers (
                id          TEXT PRIMARY KEY,
                owner       TEXT NOT NULL,
                name        TEXT NOT NULL,
                description TEXT NOT NULL DEFAULT '',
                config      TEXT NOT NULL,
                created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
                updated_at  TIMESTAMPTZ NOT NULL DEFAULT now()
            );
            CREATE INDEX IF NOT EXISTS idx_mcp_servers_owner ON mcp_servers(owner);
            """;
        await using var cmd = _db.CreateCommand(ddl);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<McpServerRecord> CreateAsync(
        string owner, SaveMcpServerRequest request, CancellationToken ct = default)
    {
        var record = new McpServerRecord
        {
            Id = Guid.NewGuid().ToString("n")[..12],
            Owner = owner,
            Name = LibraryValidation.ValidateMcpServerName(request.Name),
            Description = LibraryValidation.ValidateDescription(request.Description),
            ConfigJson = LibraryValidation.ValidateMcpServerConfig(request.ConfigJson)
        };

        const string sql = """
            INSERT INTO mcp_servers (id, owner, name, description, config)
            VALUES (@id, @owner, @name, @description, @config)
            RETURNING created_at, updated_at
            """;
        await using var cmd = _db.CreateCommand(sql);
        cmd.Parameters.AddWithValue("id", record.Id);
        cmd.Parameters.AddWithValue("owner", record.Owner);
        cmd.Parameters.AddWithValue("name", record.Name);
        cmd.Parameters.AddWithValue("description", record.Description);
        cmd.Parameters.AddWithValue("config", record.ConfigJson);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return new McpServerRecord
        {
            Id = record.Id, Owner = record.Owner, Name = record.Name,
            Description = record.Description, ConfigJson = record.ConfigJson,
            CreatedAt = reader.GetDateTime(0), UpdatedAt = reader.GetDateTime(1)
        };
    }

    public async Task<McpServerRecord> UpdateAsync(
        string owner, string id, SaveMcpServerRequest request, CancellationToken ct = default)
    {
        var name = LibraryValidation.ValidateMcpServerName(request.Name);
        var description = LibraryValidation.ValidateDescription(request.Description);
        var config = LibraryValidation.ValidateMcpServerConfig(request.ConfigJson);

        const string sql = """
            UPDATE mcp_servers
            SET name = @name, description = @description, config = @config, updated_at = now()
            WHERE id = @id AND owner = @owner
            RETURNING created_at, updated_at
            """;
        await using var cmd = _db.CreateCommand(sql);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("owner", owner);
        cmd.Parameters.AddWithValue("name", name);
        cmd.Parameters.AddWithValue("description", description);
        cmd.Parameters.AddWithValue("config", config);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new KeyNotFoundException();
        return new McpServerRecord
        {
            Id = id, Owner = owner, Name = name, Description = description, ConfigJson = config,
            CreatedAt = reader.GetDateTime(0), UpdatedAt = reader.GetDateTime(1)
        };
    }

    public async Task DeleteAsync(string owner, string id, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand(
            "DELETE FROM mcp_servers WHERE id = @id AND owner = @owner");
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("owner", owner);
        if (await cmd.ExecuteNonQueryAsync(ct) == 0)
            throw new KeyNotFoundException();
    }

    public async Task<IReadOnlyList<McpServerRecord>> ListByOwnerAsync(
        string owner, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand($"{SelectBase} WHERE owner = @owner ORDER BY name");
        cmd.Parameters.AddWithValue("owner", owner);
        return await QueryAsync(cmd, ct);
    }

    /// <summary>Fetches records by id regardless of owner — access is the caller's concern.</summary>
    public async Task<IReadOnlyList<McpServerRecord>> GetManyAsync(
        IReadOnlyCollection<string> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return [];
        await using var cmd = _db.CreateCommand($"{SelectBase} WHERE id = ANY(@ids) ORDER BY name");
        cmd.Parameters.AddWithValue("ids", ids.ToArray());
        return await QueryAsync(cmd, ct);
    }

    private const string SelectBase =
        "SELECT id, owner, name, description, config, created_at, updated_at FROM mcp_servers";

    private static async Task<IReadOnlyList<McpServerRecord>> QueryAsync(
        NpgsqlCommand cmd, CancellationToken ct)
    {
        var list = new List<McpServerRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new McpServerRecord
            {
                Id = reader.GetString(0),
                Owner = reader.GetString(1),
                Name = reader.GetString(2),
                Description = reader.GetString(3),
                ConfigJson = reader.GetString(4),
                CreatedAt = reader.GetDateTime(5),
                UpdatedAt = reader.GetDateTime(6)
            });
        }
        return list;
    }
}
