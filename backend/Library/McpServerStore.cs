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

/// <summary>Postgres persistence for the personal/org MCP server catalog.</summary>
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
                kind        TEXT NOT NULL DEFAULT 'raw',
                config_json TEXT NOT NULL,
                secret_json TEXT,
                created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
                updated_at  TIMESTAMPTZ NOT NULL DEFAULT now()
            );
            CREATE INDEX IF NOT EXISTS idx_mcp_servers_owner ON mcp_servers(owner);
            CREATE UNIQUE INDEX IF NOT EXISTS idx_mcp_servers_owner_name
                ON mcp_servers (owner, lower(name));
            """;
        await using var cmd = _db.CreateCommand(ddl);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<McpServerRecord> CreateAsync(
        string owner, SaveMcpServerRequest request, CancellationToken ct = default)
    {
        var kind = LibraryValidation.ValidateKind(request.Kind);
        var record = new McpServerRecord
        {
            Id = Guid.NewGuid().ToString("n")[..12],
            Owner = owner,
            Name = LibraryValidation.ValidateMcpServerName(request.Name),
            Description = LibraryValidation.ValidateDescription(request.Description),
            Kind = kind,
            ConfigJson = LibraryValidation.ValidateMcpServerConfig(request.ConfigJson, kind),
            SecretJson = request.SecretJson
        };

        const string sql = """
            INSERT INTO mcp_servers (id, owner, name, description, kind, config_json, secret_json)
            VALUES (@id, @owner, @name, @description, @kind, @config_json, @secret_json)
            RETURNING created_at, updated_at
            """;
        await using var cmd = _db.CreateCommand(sql);
        cmd.Parameters.AddWithValue("id", record.Id);
        cmd.Parameters.AddWithValue("owner", record.Owner);
        cmd.Parameters.AddWithValue("name", record.Name);
        cmd.Parameters.AddWithValue("description", record.Description);
        cmd.Parameters.AddWithValue("kind", record.Kind);
        cmd.Parameters.AddWithValue("config_json", record.ConfigJson);
        cmd.Parameters.AddWithValue("secret_json", (object?)record.SecretJson ?? DBNull.Value);
        try
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            return new McpServerRecord
            {
                Id = record.Id,
                Owner = record.Owner,
                Name = record.Name,
                Description = record.Description,
                Kind = record.Kind,
                ConfigJson = record.ConfigJson,
                SecretJson = record.SecretJson,
                CreatedAt = reader.GetDateTime(0),
                UpdatedAt = reader.GetDateTime(1)
            };
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new ArgumentException("An MCP server with this name already exists for this owner.");
        }
    }

    public async Task<McpServerRecord> UpdateAsync(
        string owner, string id, SaveMcpServerRequest request, CancellationToken ct = default)
    {
        var kind = LibraryValidation.ValidateKind(request.Kind);
        var name = LibraryValidation.ValidateMcpServerName(request.Name);
        var description = LibraryValidation.ValidateDescription(request.Description);
        var config = LibraryValidation.ValidateMcpServerConfig(request.ConfigJson, kind);
        // null = leave unchanged; "" = clear; otherwise replace.
        var clearSecret = request.SecretJson is not null && request.SecretJson.Length == 0;
        var setSecret = request.SecretJson is not null && request.SecretJson.Length > 0;

        const string sql = """
            UPDATE mcp_servers
            SET name = @name, description = @description, kind = @kind,
                config_json = @config_json,
                secret_json = CASE
                    WHEN @clear_secret THEN NULL
                    WHEN @set_secret THEN @secret_json
                    ELSE secret_json
                END,
                updated_at = now()
            WHERE id = @id AND owner = @owner
            RETURNING created_at, updated_at, secret_json
            """;
        await using var cmd = _db.CreateCommand(sql);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("owner", owner);
        cmd.Parameters.AddWithValue("name", name);
        cmd.Parameters.AddWithValue("description", description);
        cmd.Parameters.AddWithValue("kind", kind);
        cmd.Parameters.AddWithValue("config_json", config);
        cmd.Parameters.AddWithValue("clear_secret", clearSecret);
        cmd.Parameters.AddWithValue("set_secret", setSecret);
        cmd.Parameters.AddWithValue("secret_json", setSecret ? request.SecretJson! : (object)DBNull.Value);
        try
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                throw new KeyNotFoundException();
            return new McpServerRecord
            {
                Id = id,
                Owner = owner,
                Name = name,
                Description = description,
                Kind = kind,
                ConfigJson = config,
                SecretJson = reader.IsDBNull(2) ? null : reader.GetString(2),
                CreatedAt = reader.GetDateTime(0),
                UpdatedAt = reader.GetDateTime(1)
            };
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new ArgumentException("An MCP server with this name already exists for this owner.");
        }
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

    public async Task<IReadOnlyList<McpServerRecord>> GetManyAsync(
        IReadOnlyCollection<string> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return [];
        await using var cmd = _db.CreateCommand($"{SelectBase} WHERE id = ANY(@ids) ORDER BY name");
        cmd.Parameters.AddWithValue("ids", ids.ToArray());
        return await QueryAsync(cmd, ct);
    }

    private const string SelectBase =
        """
        SELECT id, owner, name, description, kind, config_json, secret_json, created_at, updated_at
        FROM mcp_servers
        """;

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
                Kind = reader.GetString(4),
                ConfigJson = reader.GetString(5),
                SecretJson = reader.IsDBNull(6) ? null : reader.GetString(6),
                CreatedAt = reader.GetDateTime(7),
                UpdatedAt = reader.GetDateTime(8)
            });
        }
        return list;
    }
}
