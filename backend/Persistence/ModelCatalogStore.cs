using System.Text.Json;
using AgentHub.Api.Services;
using Npgsql;

namespace AgentHub.Api.Persistence;

public sealed record StoredModelCatalog(ModelCatalog Catalog, DateTimeOffset FetchedAt);

/// <summary>The last catalog per owner and account (docs/design/model-discovery.md).</summary>
public interface IModelCatalogStore
{
    Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
    Task<StoredModelCatalog?> GetAsync(ModelCatalogKey key, CancellationToken ct = default);
    Task PutAsync(ModelCatalogKey key, ModelCatalog catalog, DateTimeOffset fetchedAt, CancellationToken ct = default);
}

public sealed class PostgresModelCatalogStore(IConfiguration configuration) : IModelCatalogStore
{
    private readonly NpgsqlDataSource _db = PostgresDataSources.Shared(configuration);

    // One row per owner, agent, mode, account and endpoint: a refresh replaces it, so the table
    // grows with the accounts people have, not with the probes. No foreign key to sessions — the
    // point is that the list outlives the session that found it.
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var command = _db.CreateCommand("""
            CREATE TABLE IF NOT EXISTS model_catalogs (
                owner TEXT NOT NULL,
                agent TEXT NOT NULL,
                auth_mode TEXT NOT NULL,
                account TEXT NOT NULL,
                endpoint TEXT NOT NULL,
                catalog TEXT NOT NULL,
                fetched_at TIMESTAMPTZ NOT NULL,
                PRIMARY KEY (owner, agent, auth_mode, account, endpoint)
            );
            """);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<StoredModelCatalog?> GetAsync(ModelCatalogKey key, CancellationToken ct = default)
    {
        await using var command = _db.CreateCommand("""
            SELECT catalog, fetched_at FROM model_catalogs
            WHERE owner = @owner AND agent = @agent AND auth_mode = @mode AND account = @account AND endpoint = @endpoint
            """);
        Bind(command, key);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var catalog = ModelCatalogSanitizer.Parse(reader.GetString(0));
        return catalog is null ? null : new StoredModelCatalog(catalog, reader.GetFieldValue<DateTimeOffset>(1));
    }

    public async Task PutAsync(ModelCatalogKey key, ModelCatalog catalog, DateTimeOffset fetchedAt, CancellationToken ct = default)
    {
        await using var command = _db.CreateCommand("""
            INSERT INTO model_catalogs (owner, agent, auth_mode, account, endpoint, catalog, fetched_at)
            VALUES (@owner, @agent, @mode, @account, @endpoint, @catalog, @at)
            ON CONFLICT (owner, agent, auth_mode, account, endpoint)
            DO UPDATE SET catalog = EXCLUDED.catalog, fetched_at = EXCLUDED.fetched_at
            """);
        Bind(command, key);
        command.Parameters.AddWithValue("catalog", JsonSerializer.Serialize(catalog, ModelCatalogSanitizer.Json));
        command.Parameters.AddWithValue("at", fetchedAt.ToUniversalTime());
        await command.ExecuteNonQueryAsync(ct);
    }

    private static void Bind(NpgsqlCommand command, ModelCatalogKey key)
    {
        command.Parameters.AddWithValue("owner", key.Owner);
        command.Parameters.AddWithValue("agent", key.Agent.ToString());
        command.Parameters.AddWithValue("mode", key.AuthMode.ToString());
        command.Parameters.AddWithValue("account", key.Account);
        command.Parameters.AddWithValue("endpoint", key.Endpoint);
    }
}
