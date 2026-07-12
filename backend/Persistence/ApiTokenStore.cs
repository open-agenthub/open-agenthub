using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace AgentHub.Api.Persistence;

/// <summary>A personal API token as shown to the user (never contains the secret itself).</summary>
public sealed class ApiTokenInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    /// <summary>Non-secret prefix for recognition, e.g. "oah_1a2b3c4d".</summary>
    public required string Prefix { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? LastUsedAt { get; init; }
}

/// <summary>
/// Store for per-user personal API tokens used to drive sessions remotely.
/// Only the SHA-256 hash of a token is persisted; the plaintext token is
/// shown to the user exactly once at creation time.
/// </summary>
public sealed class ApiTokenStore
{
    private readonly NpgsqlDataSource _db;

    public ApiTokenStore(IConfiguration cfg)
    {
        var cs = cfg.GetConnectionString("Postgres")
                 ?? throw new InvalidOperationException("ConnectionStrings:Postgres is missing.");
        _db = NpgsqlDataSource.Create(cs);
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        const string ddl = """
            CREATE TABLE IF NOT EXISTS api_tokens (
                id           TEXT PRIMARY KEY,
                owner        TEXT NOT NULL,
                name         TEXT NOT NULL,
                token_hash   TEXT NOT NULL,
                token_prefix TEXT NOT NULL,
                created_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
                last_used_at TIMESTAMPTZ
            );
            CREATE INDEX IF NOT EXISTS idx_api_tokens_owner ON api_tokens(owner);
            CREATE INDEX IF NOT EXISTS idx_api_tokens_hash ON api_tokens(token_hash);
            """;
        await using var cmd = _db.CreateCommand(ddl);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>SHA-256 hex of the full token — the only representation we persist.</summary>
    public static string Hash(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    public async Task<ApiTokenInfo> CreateAsync(string owner, string name, string token, CancellationToken ct = default)
    {
        var id = Guid.NewGuid().ToString("n");
        // Recognizable, non-secret prefix: "oah_" plus the first 8 chars of the random part.
        var prefix = token.Length >= 12 ? token[..12] : token;
        var createdAt = DateTime.UtcNow;

        const string sql = """
            INSERT INTO api_tokens (id, owner, name, token_hash, token_prefix, created_at)
            VALUES (@id, @owner, @name, @hash, @prefix, @created);
            """;
        await using var cmd = _db.CreateCommand(sql);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("owner", owner);
        cmd.Parameters.AddWithValue("name", name);
        cmd.Parameters.AddWithValue("hash", Hash(token));
        cmd.Parameters.AddWithValue("prefix", prefix);
        cmd.Parameters.AddWithValue("created", createdAt);
        await cmd.ExecuteNonQueryAsync(ct);

        return new ApiTokenInfo { Id = id, Name = name, Prefix = prefix, CreatedAt = createdAt, LastUsedAt = null };
    }

    public async Task<IReadOnlyList<ApiTokenInfo>> ListByOwnerAsync(string owner, CancellationToken ct = default)
    {
        var list = new List<ApiTokenInfo>();
        await using var cmd = _db.CreateCommand(
            "SELECT id, name, token_prefix, created_at, last_used_at FROM api_tokens WHERE owner=@owner ORDER BY created_at DESC");
        cmd.Parameters.AddWithValue("owner", owner);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new ApiTokenInfo
            {
                Id = r.GetString(0),
                Name = r.GetString(1),
                Prefix = r.GetString(2),
                CreatedAt = r.GetDateTime(3),
                LastUsedAt = r.IsDBNull(4) ? null : r.GetDateTime(4)
            });
        return list;
    }

    public async Task<bool> DeleteAsync(string owner, string id, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand("DELETE FROM api_tokens WHERE id=@id AND owner=@owner");
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("owner", owner);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    /// <summary>
    /// Resolves a plaintext token to its owner (or null if unknown) and stamps last_used_at.
    /// </summary>
    public async Task<string?> FindOwnerByTokenAsync(string token, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE api_tokens SET last_used_at = now()
            WHERE token_hash = @hash
            RETURNING owner;
            """;
        await using var cmd = _db.CreateCommand(sql);
        cmd.Parameters.AddWithValue("hash", Hash(token));
        var v = await cmd.ExecuteScalarAsync(ct);
        return v as string;
    }
}
