using Npgsql;

namespace AgentHub.Api.Persistence;

public sealed record SessionWorkspace(string SessionId, string ClaimName, string? ClaimUid,
    string Size, string? StorageClass, string Nonce, string Repositories);

public interface ISessionWorkspaceStore
{
    Task InitializeAsync(CancellationToken ct = default);
    Task<SessionWorkspace?> GetAsync(string owner, string id, CancellationToken ct = default);
    Task AddAsync(string owner, SessionWorkspace workspace, CancellationToken ct = default);
    Task BindAsync(string owner, string id, string uid, CancellationToken ct = default);
}

public sealed class PostgresSessionWorkspaceStore(IConfiguration configuration) : ISessionWorkspaceStore
{
    private readonly NpgsqlDataSource _db = PostgresDataSources.Shared(configuration);
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var command = _db.CreateCommand("""
            CREATE TABLE IF NOT EXISTS session_workspaces (
                session_id TEXT PRIMARY KEY REFERENCES sessions(id) ON DELETE CASCADE,
                claim_name TEXT NOT NULL UNIQUE, claim_uid TEXT,
                size TEXT NOT NULL, storage_class TEXT, nonce TEXT NOT NULL,
                repositories TEXT NOT NULL
            )
            """);
        await command.ExecuteNonQueryAsync(ct);
    }
    public async Task<SessionWorkspace?> GetAsync(string owner, string id, CancellationToken ct = default)
    {
        await using var command = _db.CreateCommand("""
            SELECT w.session_id,w.claim_name,w.claim_uid,w.size,w.storage_class,w.nonce,w.repositories
            FROM session_workspaces w JOIN sessions s ON s.id=w.session_id WHERE s.id=@id AND s.owner=@owner
            """);
        command.Parameters.AddWithValue("id", id); command.Parameters.AddWithValue("owner", owner);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new(reader.GetString(0), reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5), reader.GetString(6)) : null;
    }
    public async Task AddAsync(string owner, SessionWorkspace workspace, CancellationToken ct = default)
    {
        await using var command = _db.CreateCommand("""
            WITH admitted AS (
                INSERT INTO session_workspaces(session_id,claim_name,size,storage_class,nonce,repositories)
                SELECT id,@claim,@size,@class,@nonce,@repos FROM sessions WHERE id=@id AND owner=@owner RETURNING session_id
            ) UPDATE sessions SET workspace_persistent=TRUE WHERE id IN (SELECT session_id FROM admitted)
            """);
        command.Parameters.AddWithValue("id", workspace.SessionId); command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("claim", workspace.ClaimName); command.Parameters.AddWithValue("size", workspace.Size);
        command.Parameters.AddWithValue("class", (object?)workspace.StorageClass ?? DBNull.Value);
        command.Parameters.AddWithValue("nonce", workspace.Nonce); command.Parameters.AddWithValue("repos", workspace.Repositories);
        if (await command.ExecuteNonQueryAsync(ct) != 1) throw new KeyNotFoundException("Session not found.");
    }
    public async Task BindAsync(string owner, string id, string uid, CancellationToken ct = default)
    {
        await using var command = _db.CreateCommand("""
            UPDATE session_workspaces w SET claim_uid=@uid FROM sessions s
            WHERE w.session_id=s.id AND s.id=@id AND s.owner=@owner AND (w.claim_uid IS NULL OR w.claim_uid=@uid)
            """);
        command.Parameters.AddWithValue("id", id); command.Parameters.AddWithValue("owner", owner); command.Parameters.AddWithValue("uid", uid);
        if (await command.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("Workspace claim identity changed.");
    }
}
