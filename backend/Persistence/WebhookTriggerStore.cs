using System.Text.Json;
using AgentHub.Api.Models;
using Npgsql;

namespace AgentHub.Api.Persistence;

/// <summary>A stored trigger as needed by the delivery endpoint (owner + protected secret).</summary>
public sealed record WebhookTriggerRecord
{
    public required string Owner { get; init; }
    /// <summary>Data-Protection payload; unprotect via IWebhookSecretProtector.</summary>
    public required string SecretProtected { get; init; }
    public required WebhookTriggerInfo Info { get; init; }
}

public interface IWebhookTriggerStore
{
    Task InitializeAsync(CancellationToken ct = default);
    Task<IReadOnlyList<WebhookTriggerInfo>> ListAsync(string owner, CancellationToken ct = default);
    /// <summary>Lookup for inbound deliveries — by id only, across all owners.</summary>
    Task<WebhookTriggerRecord?> GetByIdAsync(string id, CancellationToken ct = default);
    Task<WebhookTriggerInfo> CreateAsync(string owner, CreateWebhookTriggerRequest request,
        string secretProtected, CancellationToken ct = default);
    Task<bool> DeleteAsync(string owner, string id, CancellationToken ct = default);
    /// <summary>Stamps last_triggered_at after a delivery started a session.</summary>
    Task TouchAsync(string id, CancellationToken ct = default);
}

public sealed class PostgresWebhookTriggerStore : IWebhookTriggerStore
{
    private readonly NpgsqlDataSource _db;

    public PostgresWebhookTriggerStore(IConfiguration cfg)
    {
        var cs = cfg.GetConnectionString("Postgres")
                 ?? throw new InvalidOperationException("ConnectionStrings:Postgres is missing.");
        _db = NpgsqlDataSource.Create(cs);
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        const string ddl = """
            CREATE TABLE IF NOT EXISTS webhook_triggers (
                id                TEXT PRIMARY KEY,
                owner             TEXT NOT NULL,
                name              TEXT NOT NULL,
                provider_id       TEXT,
                secret            TEXT NOT NULL,
                events            TEXT NOT NULL,
                repo_filter       TEXT,
                prompt_template   TEXT NOT NULL,
                project_id        TEXT,
                agent             TEXT NOT NULL,
                auto_approve      BOOLEAN NOT NULL DEFAULT FALSE,
                created_at        TIMESTAMPTZ NOT NULL DEFAULT now(),
                last_triggered_at TIMESTAMPTZ
            );
            CREATE INDEX IF NOT EXISTS idx_webhook_triggers_owner ON webhook_triggers(owner);
            """;
        await using var cmd = _db.CreateCommand(ddl);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private const string Columns =
        "id, name, provider_id, events, repo_filter, prompt_template, project_id, agent, " +
        "auto_approve, created_at, last_triggered_at";

    public async Task<IReadOnlyList<WebhookTriggerInfo>> ListAsync(string owner, CancellationToken ct = default)
    {
        var triggers = new List<WebhookTriggerInfo>();
        await using var cmd = _db.CreateCommand(
            $"SELECT {Columns} FROM webhook_triggers WHERE owner = @owner ORDER BY created_at, id");
        cmd.Parameters.AddWithValue("owner", owner);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) triggers.Add(MapInfo(reader));
        return triggers;
    }

    public async Task<WebhookTriggerRecord?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand(
            $"SELECT {Columns}, owner, secret FROM webhook_triggers WHERE id = @id");
        cmd.Parameters.AddWithValue("id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new WebhookTriggerRecord
        {
            Owner = reader.GetString(11),
            SecretProtected = reader.GetString(12),
            Info = MapInfo(reader)
        };
    }

    public async Task<WebhookTriggerInfo> CreateAsync(string owner, CreateWebhookTriggerRequest request,
        string secretProtected, CancellationToken ct = default)
    {
        var id = Guid.NewGuid().ToString("n");
        await using var cmd = _db.CreateCommand($"""
            INSERT INTO webhook_triggers
                (id, owner, name, provider_id, secret, events, repo_filter, prompt_template,
                 project_id, agent, auto_approve)
            VALUES (@id, @owner, @name, @providerId, @secret, @events, @repoFilter, @promptTemplate,
                 @projectId, @agent, @autoApprove)
            RETURNING {Columns}
            """);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("owner", owner);
        cmd.Parameters.AddWithValue("name", request.Name.Trim());
        cmd.Parameters.AddWithValue("providerId", (object?)request.ProviderId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("secret", secretProtected);
        cmd.Parameters.AddWithValue("events", JsonSerializer.Serialize(request.Events));
        cmd.Parameters.AddWithValue("repoFilter", (object?)request.RepoFilter ?? DBNull.Value);
        cmd.Parameters.AddWithValue("promptTemplate", request.PromptTemplate);
        cmd.Parameters.AddWithValue("projectId", (object?)request.ProjectId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("agent", request.Agent.ToString());
        cmd.Parameters.AddWithValue("autoApprove", request.AutoApprove);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return MapInfo(reader);
    }

    public async Task<bool> DeleteAsync(string owner, string id, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand(
            "DELETE FROM webhook_triggers WHERE owner = @owner AND id = @id");
        cmd.Parameters.AddWithValue("owner", owner);
        cmd.Parameters.AddWithValue("id", id);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task TouchAsync(string id, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand(
            "UPDATE webhook_triggers SET last_triggered_at = now() WHERE id = @id");
        cmd.Parameters.AddWithValue("id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static WebhookTriggerInfo MapInfo(NpgsqlDataReader reader) => new()
    {
        Id = reader.GetString(0),
        Name = reader.GetString(1),
        ProviderId = reader.IsDBNull(2) ? null : reader.GetString(2),
        Events = DeserializeEvents(reader.GetString(3)),
        RepoFilter = reader.IsDBNull(4) ? null : reader.GetString(4),
        PromptTemplate = reader.GetString(5),
        ProjectId = reader.IsDBNull(6) ? null : reader.GetString(6),
        Agent = Enum.TryParse<AgentKind>(reader.GetString(7), out var agent) ? agent : AgentKind.Claude,
        AutoApprove = reader.GetBoolean(8),
        CreatedAt = reader.GetDateTime(9),
        LastTriggeredAt = reader.IsDBNull(10) ? null : reader.GetDateTime(10)
    };

    private static IReadOnlyList<string> DeserializeEvents(string json)
    {
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch (JsonException) { return []; }
    }
}
