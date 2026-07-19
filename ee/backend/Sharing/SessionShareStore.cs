using System.Text.Json;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using Npgsql;
using NpgsqlTypes;

namespace AgentHub.Api.Ee.Sharing;

public sealed class SessionShareStore : ISessionAccessStore, ISessionMcpPolicyReader
{
    private readonly NpgsqlDataSource _db;
    private readonly ILogger<SessionShareStore> _logger;

    public SessionShareStore(IConfiguration configuration, ILogger<SessionShareStore> logger)
    {
        var connectionString = configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("ConnectionStrings:Postgres is missing.");
        _db = NpgsqlDataSource.Create(connectionString);
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        const string ddl = """
            CREATE TABLE IF NOT EXISTS session_shares (
                session_id       TEXT NOT NULL,
                recipient_owner  TEXT NOT NULL,
                role             TEXT NOT NULL CHECK (role IN ('Viewer', 'Collaborator')),
                created_at       TIMESTAMPTZ NOT NULL DEFAULT now(),
                updated_at       TIMESTAMPTZ NOT NULL DEFAULT now(),
                PRIMARY KEY (session_id, recipient_owner)
            );
            CREATE INDEX IF NOT EXISTS idx_session_shares_recipient
                ON session_shares(recipient_owner);

            CREATE TABLE IF NOT EXISTS session_share_links (
                id            TEXT PRIMARY KEY,
                session_id    TEXT NOT NULL,
                token_hash    BYTEA UNIQUE NOT NULL,
                role          TEXT NOT NULL CHECK (role IN ('Viewer', 'Collaborator')),
                expires_at    TIMESTAMPTZ,
                created_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
                updated_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
                last_used_at  TIMESTAMPTZ
            );
            CREATE INDEX IF NOT EXISTS idx_session_share_links_session
                ON session_share_links(session_id);

            CREATE TABLE IF NOT EXISTS session_mcp_policies (
                session_id       TEXT PRIMARY KEY,
                blocked_servers  TEXT NOT NULL,
                blocked_tools    TEXT NOT NULL,
                updated_at       TIMESTAMPTZ NOT NULL DEFAULT now()
            );
            """;

        await using var command = _db.CreateCommand(ddl);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<SessionSharingOverview> ListForOwnerAsync(
        string owner,
        string sessionId,
        CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await EnsureOwnerAsync(connection, transaction, owner, sessionId, ct);

        var users = new List<DirectSessionShare>();
        const string usersSql = """
            SELECT recipient_owner, role, created_at, updated_at
            FROM session_shares
            WHERE session_id = @session
            ORDER BY recipient_owner
            """;
        await using (var command = new NpgsqlCommand(usersSql, connection, transaction))
        {
            command.Parameters.AddWithValue("session", sessionId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                users.Add(new DirectSessionShare(
                    reader.GetString(0),
                    ParseRole(reader.GetString(1)),
                    reader.GetDateTime(2),
                    reader.GetDateTime(3)));
            }
        }

        var links = new List<SessionShareLink>();
        const string linksSql = """
            SELECT id, role, expires_at, created_at, updated_at, last_used_at
            FROM session_share_links
            WHERE session_id = @session
            ORDER BY created_at DESC
            """;
        await using (var command = new NpgsqlCommand(linksSql, connection, transaction))
        {
            command.Parameters.AddWithValue("session", sessionId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                links.Add(MapLink(reader));
        }

        var policy = await GetMcpPolicyAsync(connection, transaction, sessionId, ct);
        await transaction.CommitAsync(ct);
        return new SessionSharingOverview(users, links, policy);
    }

    public async Task<IReadOnlyList<StoredSessionAccess>> ListSharedWithAsync(
        string recipient,
        CancellationToken ct = default)
    {
        var result = new List<StoredSessionAccess>();
        const string sql = $"""
            SELECT {SessionColumns}, shares.role
            FROM sessions
            JOIN session_shares shares ON shares.session_id = sessions.id
            WHERE shares.recipient_owner = @recipient
              AND sessions.owner <> @recipient
            ORDER BY sessions.created_at DESC
            """;

        await using var command = _db.CreateCommand(sql);
        command.Parameters.AddWithValue("recipient", recipient);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new StoredSessionAccess(MapSession(reader), ParseRole(reader.GetString(18))));
        return result;
    }

    public async Task<DirectSessionShare> UpsertDirectAsync(
        string owner,
        string sessionId,
        string recipient,
        ShareRole role,
        CancellationToken ct = default)
    {
        ValidateRole(role);
        if (string.IsNullOrWhiteSpace(recipient))
            throw new ArgumentException("Recipient is required.", nameof(recipient));
        if (string.Equals(owner, recipient, StringComparison.Ordinal))
            throw new ArgumentException("A session owner cannot share with themselves.", nameof(recipient));

        await using var connection = await _db.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await EnsureOwnerAsync(connection, transaction, owner, sessionId, ct);
        await EnsureRecipientAsync(connection, transaction, recipient, ct);

        const string sql = """
            INSERT INTO session_shares (session_id, recipient_owner, role)
            VALUES (@session, @recipient, @role)
            ON CONFLICT (session_id, recipient_owner) DO UPDATE SET
                role = EXCLUDED.role,
                updated_at = now()
            RETURNING created_at, updated_at
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("session", sessionId);
        command.Parameters.AddWithValue("recipient", recipient);
        command.Parameters.AddWithValue("role", role.ToString());
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        var result = new DirectSessionShare(
            recipient,
            role,
            reader.GetDateTime(0),
            reader.GetDateTime(1));

        await transaction.CommitAsync(ct);
        return result;
    }

    public async Task DeleteDirectAsync(
        string owner,
        string sessionId,
        string recipient,
        CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await EnsureOwnerAsync(connection, transaction, owner, sessionId, ct);

        await using var command = new NpgsqlCommand(
            "DELETE FROM session_shares WHERE session_id = @session AND recipient_owner = @recipient",
            connection,
            transaction);
        command.Parameters.AddWithValue("session", sessionId);
        command.Parameters.AddWithValue("recipient", recipient);
        if (await command.ExecuteNonQueryAsync(ct) == 0)
            throw new KeyNotFoundException();

        await transaction.CommitAsync(ct);
    }

    public async Task<IssuedSessionShareLink> CreateLinkAsync(
        string owner,
        string sessionId,
        ShareRole role,
        DateTime? expiresAt,
        CancellationToken ct = default)
    {
        ValidateRole(role);
        ValidateExpiration(expiresAt);
        var issued = ShareTokens.Issue();
        var id = Guid.NewGuid().ToString("N");

        await using var connection = await _db.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await EnsureOwnerAsync(connection, transaction, owner, sessionId, ct);

        const string sql = """
            INSERT INTO session_share_links (id, session_id, token_hash, role, expires_at)
            VALUES (@id, @session, @hash, @role, @expires)
            RETURNING created_at, updated_at
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("session", sessionId);
        command.Parameters.Add("hash", NpgsqlDbType.Bytea).Value = issued.Hash;
        command.Parameters.AddWithValue("role", role.ToString());
        command.Parameters.AddWithValue("expires", (object?)expiresAt ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        var link = new SessionShareLink(
            id,
            role,
            expiresAt,
            reader.GetDateTime(0),
            reader.GetDateTime(1),
            null);

        await transaction.CommitAsync(ct);
        return new IssuedSessionShareLink(link, issued.Token);
    }

    public async Task<SessionShareLink> UpdateLinkAsync(
        string owner,
        string sessionId,
        string linkId,
        ShareRole role,
        DateTime? expiresAt,
        CancellationToken ct = default)
    {
        ValidateRole(role);
        ValidateExpiration(expiresAt);

        await using var connection = await _db.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await EnsureOwnerAsync(connection, transaction, owner, sessionId, ct);

        const string sql = """
            UPDATE session_share_links
            SET role = @role, expires_at = @expires, updated_at = now()
            WHERE id = @id AND session_id = @session
            RETURNING id, role, expires_at, created_at, updated_at, last_used_at
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("id", linkId);
        command.Parameters.AddWithValue("session", sessionId);
        command.Parameters.AddWithValue("role", role.ToString());
        command.Parameters.AddWithValue("expires", (object?)expiresAt ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new KeyNotFoundException();
        var result = MapLink(reader);

        await transaction.CommitAsync(ct);
        return result;
    }

    public async Task DeleteLinkAsync(
        string owner,
        string sessionId,
        string linkId,
        CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await EnsureOwnerAsync(connection, transaction, owner, sessionId, ct);

        await using var command = new NpgsqlCommand(
            "DELETE FROM session_share_links WHERE id = @id AND session_id = @session",
            connection,
            transaction);
        command.Parameters.AddWithValue("id", linkId);
        command.Parameters.AddWithValue("session", sessionId);
        if (await command.ExecuteNonQueryAsync(ct) == 0)
            throw new KeyNotFoundException();

        await transaction.CommitAsync(ct);
    }

    public async Task<SessionMcpPolicy?> GetMcpPolicyAsync(
        string sessionId,
        CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct);
        return await GetMcpPolicyAsync(connection, null, sessionId, ct);
    }

    public async Task<SessionMcpPolicy?> SetMcpPolicyAsync(
        string owner,
        string sessionId,
        IReadOnlyCollection<string>? blockedServers,
        IReadOnlyCollection<string>? blockedTools,
        CancellationToken ct = default)
    {
        var servers = NormalizeServers(blockedServers);
        var tools = NormalizeTools(blockedTools);

        await using var connection = await _db.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await EnsureOwnerAsync(connection, transaction, owner, sessionId, ct);

        if (servers.Length == 0 && tools.Length == 0)
        {
            await DeleteMcpPolicyAsync(connection, transaction, sessionId, ct);
            await transaction.CommitAsync(ct);
            return null;
        }

        const string sql = """
            INSERT INTO session_mcp_policies (session_id, blocked_servers, blocked_tools)
            VALUES (@session, @servers, @tools)
            ON CONFLICT (session_id) DO UPDATE SET
                blocked_servers = EXCLUDED.blocked_servers,
                blocked_tools = EXCLUDED.blocked_tools,
                updated_at = now()
            RETURNING updated_at
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("session", sessionId);
        command.Parameters.AddWithValue("servers", JsonSerializer.Serialize(servers));
        command.Parameters.AddWithValue("tools", JsonSerializer.Serialize(tools));
        var updatedAt = (DateTime)(await command.ExecuteScalarAsync(ct)
            ?? throw new InvalidOperationException("MCP policy update returned no timestamp."));

        await transaction.CommitAsync(ct);
        return new SessionMcpPolicy(servers, tools, updatedAt);
    }

    public async Task DeleteMcpPolicyAsync(
        string owner,
        string sessionId,
        CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await EnsureOwnerAsync(connection, transaction, owner, sessionId, ct);
        await DeleteMcpPolicyAsync(connection, transaction, sessionId, ct);
        await transaction.CommitAsync(ct);
    }

    public async Task DeleteForSessionAsync(string sessionId, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        foreach (var table in new[] { "session_shares", "session_share_links", "session_mcp_policies" })
        {
            await using var command = new NpgsqlCommand(
                $"DELETE FROM {table} WHERE session_id = @session",
                connection,
                transaction);
            command.Parameters.AddWithValue("session", sessionId);
            await command.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    public async Task<StoredSessionAccess?> FindUserAccessAsync(
        string principal,
        string sessionId,
        CancellationToken ct = default)
    {
        const string sql = $"""
            SELECT {SessionColumns}, shares.role
            FROM sessions
            LEFT JOIN session_shares shares
              ON shares.session_id = sessions.id
             AND shares.recipient_owner = @principal
            WHERE sessions.id = @session
              AND (sessions.owner = @principal OR shares.recipient_owner IS NOT NULL)
            """;

        await using var command = _db.CreateCommand(sql);
        command.Parameters.AddWithValue("principal", principal);
        command.Parameters.AddWithValue("session", sessionId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        ShareRole? role = reader.IsDBNull(18) ? null : ParseRole(reader.GetString(18));
        return new StoredSessionAccess(MapSession(reader), role);
    }

    public async Task<StoredSessionAccess?> FindTokenAccessAsync(
        string token,
        CancellationToken ct = default)
    {
        if (!ShareTokens.TryHash(token, out var tokenHash))
            return null;

        const string sql = $"""
            SELECT {SessionColumns}, links.role, links.id, links.token_hash
            FROM session_share_links links
            JOIN sessions ON sessions.id = links.session_id
            WHERE links.token_hash = @hash
              AND (links.expires_at IS NULL OR links.expires_at > now())
            """;

        await using var connection = await _db.OpenConnectionAsync(ct);
        SessionRecord session;
        ShareRole role;
        string linkId;
        byte[] storedHash;
        await using (var command = new NpgsqlCommand(sql, connection))
        {
            command.Parameters.Add("hash", NpgsqlDbType.Bytea).Value = tokenHash;
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return null;

            session = MapSession(reader);
            role = ParseRole(reader.GetString(18));
            linkId = reader.GetString(19);
            storedHash = (byte[])reader.GetValue(20);
        }

        if (!ShareTokens.Matches(token, storedHash))
            return null;

        try
        {
            await using var update = new NpgsqlCommand(
                "UPDATE session_share_links SET last_used_at = now() WHERE id = @id",
                connection);
            update.Parameters.AddWithValue("id", linkId);
            await update.ExecuteNonQueryAsync(ct);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Could not update last-used timestamp for share link {LinkId}.",
                linkId);
        }

        return new StoredSessionAccess(session, role);
    }

    private static async Task EnsureOwnerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string owner,
        string sessionId,
        CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(
            "SELECT 1 FROM sessions WHERE id = @session AND owner = @owner",
            connection,
            transaction);
        command.Parameters.AddWithValue("session", sessionId);
        command.Parameters.AddWithValue("owner", owner);
        if (await command.ExecuteScalarAsync(ct) is null)
            throw new KeyNotFoundException();
    }

    private static async Task EnsureRecipientAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string recipient,
        CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(
            "SELECT 1 FROM app_users WHERE owner = @recipient",
            connection,
            transaction);
        command.Parameters.AddWithValue("recipient", recipient);
        if (await command.ExecuteScalarAsync(ct) is null)
            throw new ArgumentException("Recipient is not a known user.", nameof(recipient));
    }

    private static async Task<SessionMcpPolicy?> GetMcpPolicyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string sessionId,
        CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT blocked_servers, blocked_tools, updated_at
            FROM session_mcp_policies
            WHERE session_id = @session
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("session", sessionId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        return new SessionMcpPolicy(
            JsonSerializer.Deserialize<string[]>(reader.GetString(0)) ?? [],
            JsonSerializer.Deserialize<string[]>(reader.GetString(1)) ?? [],
            reader.GetDateTime(2));
    }

    private static async Task DeleteMcpPolicyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sessionId,
        CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(
            "DELETE FROM session_mcp_policies WHERE session_id = @session",
            connection,
            transaction);
        command.Parameters.AddWithValue("session", sessionId);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static void ValidateRole(ShareRole role)
    {
        if (!Enum.IsDefined(role))
            throw new ArgumentException("Share role must be Viewer or Collaborator.", nameof(role));
    }

    private static void ValidateExpiration(DateTime? expiresAt)
    {
        if (expiresAt is { } expiration && expiration.ToUniversalTime() <= DateTime.UtcNow)
            throw new ArgumentException("Expiration must be in the future.", nameof(expiresAt));
    }

    private static string[] NormalizeServers(IReadOnlyCollection<string>? values)
    {
        if (values is null)
            throw new ArgumentException("Blocked servers are required.", nameof(values));

        var result = new List<string>();
        foreach (var value in values)
        {
            var server = value?.Trim();
            if (string.IsNullOrEmpty(server)
                || server.StartsWith("mcp__", StringComparison.Ordinal)
                || server.Contains("__", StringComparison.Ordinal)
                || server.Any(char.IsWhiteSpace))
            {
                throw new ArgumentException("Blocked server names must be plain MCP server names.", nameof(values));
            }
            result.Add(server);
        }
        return result.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static string[] NormalizeTools(IReadOnlyCollection<string>? values)
    {
        if (values is null)
            throw new ArgumentException("Blocked tools are required.", nameof(values));

        var result = new List<string>();
        foreach (var value in values)
        {
            var tool = value?.Trim();
            var separator = tool?.IndexOf("__", "mcp__".Length, StringComparison.Ordinal) ?? -1;
            if (string.IsNullOrEmpty(tool)
                || !tool.StartsWith("mcp__", StringComparison.Ordinal)
                || separator <= "mcp__".Length
                || separator + 2 >= tool.Length
                || tool.Any(char.IsWhiteSpace))
            {
                throw new ArgumentException("Blocked tools must use a full mcp__server__tool name.", nameof(values));
            }
            result.Add(tool);
        }
        return result.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static ShareRole ParseRole(string value)
        => Enum.TryParse<ShareRole>(value, ignoreCase: false, out var role) && Enum.IsDefined(role)
            ? role
            : throw new InvalidOperationException("Stored share role is invalid.");

    private static SessionShareLink MapLink(NpgsqlDataReader reader) => new(
        reader.GetString(0),
        ParseRole(reader.GetString(1)),
        reader.IsDBNull(2) ? null : reader.GetDateTime(2),
        reader.GetDateTime(3),
        reader.GetDateTime(4),
        reader.IsDBNull(5) ? null : reader.GetDateTime(5));

    private const string SessionColumns = """
        sessions.id, sessions.owner, sessions.title, sessions.mode, sessions.repo_url,
        sessions.schedule, sessions.claude_session_id, sessions.status,
        sessions.question_pending, sessions.callback_token, sessions.created_at,
        sessions.updated_at, sessions.image, sessions.run_as_root, sessions.cpu,
        sessions.memory, sessions.mcp_config, sessions.repos
        """;

    private static SessionRecord MapSession(NpgsqlDataReader reader) => new()
    {
        Id = reader.GetString(0),
        Owner = reader.GetString(1),
        Title = reader.GetString(2),
        Mode = Enum.Parse<SessionMode>(reader.GetString(3)),
        RepoUrl = reader.IsDBNull(4) ? null : reader.GetString(4),
        Schedule = reader.IsDBNull(5) ? null : reader.GetString(5),
        ClaudeSessionId = reader.GetString(6),
        Status = reader.GetString(7),
        QuestionPending = reader.GetBoolean(8),
        CallbackToken = reader.GetString(9),
        CreatedAt = reader.GetDateTime(10),
        UpdatedAt = reader.GetDateTime(11),
        Image = reader.IsDBNull(12) ? null : reader.GetString(12),
        RunAsRoot = reader.GetBoolean(13),
        Cpu = reader.GetString(14),
        Memory = reader.GetString(15),
        McpConfigJson = reader.IsDBNull(16) ? null : reader.GetString(16),
        ReposJson = reader.IsDBNull(17) ? null : reader.GetString(17)
    };
}
