// -----------------------------------------------------------------------------
// Open AgentHub Enterprise Edition — Session sharing.
// Part of the Enterprise Edition; NOT covered by the AGPL-3.0 license of the
// open-core. Source-available under the Open AgentHub Enterprise License
// (see ee/LICENSE); a valid subscription is required for production use.
// -----------------------------------------------------------------------------
using System.Text.Json;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using Npgsql;
using NpgsqlTypes;

namespace AgentHub.Api.Ee.Sharing;

public sealed class SessionShareStore : ISessionAccessStore, ISessionMcpPolicyReader, ISessionShareStore,
    ISessionShareStatus, IProjectShareStore
{
    private readonly NpgsqlDataSource _db;
    private readonly ILogger<SessionShareStore> _logger;

    public SessionShareStore(IConfiguration configuration, ILogger<SessionShareStore> logger)
    {
        _db = PostgresDataSources.Shared(configuration);
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        const string ddl = """
            -- Take the parent lock first: replica startup and cascades must use the same lock order.
            LOCK TABLE sessions IN SHARE ROW EXCLUSIVE MODE;
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

            -- Project shares (#409). The foreign key removes them with their project, so a project
            -- deleted and later recreated under the same id never inherits the old grants. The
            -- projects table is created by PostgresProjectStore, which initialises first.
            CREATE TABLE IF NOT EXISTS project_shares (
                project_id    TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
                subject_type  TEXT NOT NULL CHECK (subject_type IN ('user', 'group')),
                subject       TEXT NOT NULL,
                role          TEXT NOT NULL CHECK (role IN ('Viewer', 'Collaborator')),
                created_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
                updated_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
                PRIMARY KEY (project_id, subject_type, subject)
            );
            CREATE INDEX IF NOT EXISTS idx_project_shares_subject
                ON project_shares(subject_type, subject);

            DO $migration$
            BEGIN
                IF NOT EXISTS (
                    SELECT 1 FROM pg_constraint
                    WHERE conrelid = 'session_shares'::regclass AND conname = 'session_shares_session_fk'
                ) THEN
                    -- Block new orphan writes until the prune and constraint commit together.
                    LOCK TABLE session_shares IN SHARE ROW EXCLUSIVE MODE;
                    DELETE FROM session_shares metadata
                    WHERE NOT EXISTS (SELECT 1 FROM sessions WHERE id = metadata.session_id);
                    ALTER TABLE session_shares ADD CONSTRAINT session_shares_session_fk
                        FOREIGN KEY (session_id) REFERENCES sessions(id) ON DELETE CASCADE;
                END IF;
            END $migration$;

            DO $migration$
            BEGIN
                IF NOT EXISTS (
                    SELECT 1 FROM pg_constraint
                    WHERE conrelid = 'session_share_links'::regclass AND conname = 'session_share_links_session_fk'
                ) THEN
                    -- Block new orphan writes until the prune and constraint commit together.
                    LOCK TABLE session_share_links IN SHARE ROW EXCLUSIVE MODE;
                    DELETE FROM session_share_links metadata
                    WHERE NOT EXISTS (SELECT 1 FROM sessions WHERE id = metadata.session_id);
                    ALTER TABLE session_share_links ADD CONSTRAINT session_share_links_session_fk
                        FOREIGN KEY (session_id) REFERENCES sessions(id) ON DELETE CASCADE;
                END IF;
            END $migration$;

            DO $migration$
            BEGIN
                IF NOT EXISTS (
                    SELECT 1 FROM pg_constraint
                    WHERE conrelid = 'session_mcp_policies'::regclass AND conname = 'session_mcp_policies_session_fk'
                ) THEN
                    -- Block new orphan writes until the prune and constraint commit together.
                    LOCK TABLE session_mcp_policies IN SHARE ROW EXCLUSIVE MODE;
                    DELETE FROM session_mcp_policies metadata
                    WHERE NOT EXISTS (SELECT 1 FROM sessions WHERE id = metadata.session_id);
                    ALTER TABLE session_mcp_policies ADD CONSTRAINT session_mcp_policies_session_fk
                        FOREIGN KEY (session_id) REFERENCES sessions(id) ON DELETE CASCADE;
                END IF;
            END $migration$;
            """;

        await using var connection = await _db.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var command = new NpgsqlCommand(ddl, connection, transaction);
        await command.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
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
        var projectShares = new List<ProjectShare>();
        const string projectSql = """
            SELECT shares.subject_type, shares.subject, shares.role, shares.created_at, shares.updated_at
            FROM sessions
            JOIN projects ON projects.id = sessions.project_id AND projects.owner = sessions.owner
            JOIN project_shares shares ON shares.project_id = projects.id
            WHERE sessions.id = @session
            ORDER BY shares.subject_type DESC, shares.subject
            """;
        await using (var command = new NpgsqlCommand(projectSql, connection, transaction))
        {
            command.Parameters.AddWithValue("session", sessionId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                projectShares.Add(MapProjectShare(reader));
        }
        await transaction.CommitAsync(ct);
        return new SessionSharingOverview(users, links, policy, projectShares);
    }

    /// <summary>
    /// Every session a direct share or a project share opens for <paramref name="recipient"/>:
    /// the shared sessions, every session in a shared project (#409) and, live, all their
    /// descendants of the same owner (#410). Links never contribute — they are link-only and add
    /// nothing to anybody's list. One row per session, carrying every grant that reaches it, so
    /// the rules rather than this query pick the role.
    /// </summary>
    public async Task<IReadOnlyList<StoredSessionAccess>> ListSharedWithAsync(
        string recipient,
        CancellationToken ct = default)
    {
        // The descent follows parent_session_id only between sessions of the same owner. A
        // parent id is whatever the creating request said, so without the owner check anybody
        // could hang a session of their own under a shared one and show it to its recipients.
        // The depth bound is SessionDescent.MaxHops, which also stops a corrupt cycle. A project
        // grant does not descend into a child in the same project: that child is a seed of its
        // own at depth 0, and following it again would only repeat the grant once per ancestor.
        const string sql = $"""
            WITH RECURSIVE seeds(id, owner, role, source, depth, project, project_name) AS (
                SELECT sessions.id, sessions.owner, shares.role, sessions.id, 0, NULL::text, NULL::text
                FROM session_shares shares
                JOIN sessions ON sessions.id = shares.session_id
                WHERE shares.recipient_owner = @principal
                  AND sessions.owner <> @principal
                UNION ALL
                SELECT sessions.id, sessions.owner, granted.role, sessions.id, 0,
                       granted.project_id, granted.name
                FROM ({ProjectGrants}) granted
                JOIN sessions
                  ON sessions.project_id = granted.project_id
                 AND sessions.owner = granted.owner
                WHERE sessions.owner <> @principal
            ),
            reach(id, owner, role, source, depth, project, project_name) AS (
                SELECT * FROM seeds
                UNION
                SELECT child.id, child.owner, reach.role, reach.source, reach.depth + 1,
                       reach.project, reach.project_name
                FROM reach
                JOIN sessions child
                  ON child.owner = reach.owner
                 AND child.parent_session_id = reach.id
                WHERE reach.depth < @maxDepth
                  AND (reach.project IS NULL OR child.project_id IS DISTINCT FROM reach.project)
            )
            SELECT {SessionColumns}, reach.role, reach.source, reach.depth, reach.project, reach.project_name
            FROM reach
            JOIN sessions ON sessions.id = reach.id
            ORDER BY sessions.created_at DESC, sessions.id, reach.depth
            """;

        var sessions = new Dictionary<string, SessionRecord>(StringComparer.Ordinal);
        var grants = new Dictionary<string, List<ShareGrant>>(StringComparer.Ordinal);
        var order = new List<string>();
        await using var command = _db.CreateCommand(sql);
        command.Parameters.AddWithValue("principal", recipient);
        command.Parameters.AddWithValue("maxDepth", SessionDescent.MaxHops);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetString(0);
            if (!sessions.ContainsKey(id))
            {
                sessions[id] = MapSession(reader);
                grants[id] = [];
                order.Add(id);
            }
            grants[id].Add(ReadGrant(reader, AfterSession));
        }
        return order.Select(id => new StoredSessionAccess(sessions[id], grants[id])).ToArray();
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
        DirectSessionShare result;
        await using (var command = new NpgsqlCommand(sql, connection, transaction))
        {
            command.Parameters.AddWithValue("session", sessionId);
            command.Parameters.AddWithValue("recipient", recipient);
            command.Parameters.AddWithValue("role", role.ToString());
            await using var reader = await command.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            result = new DirectSessionShare(
                recipient,
                role,
                reader.GetDateTime(0),
                reader.GetDateTime(1));
        }

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
        SessionShareLink link;
        await using (var command = new NpgsqlCommand(sql, connection, transaction))
        {
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("session", sessionId);
            command.Parameters.Add("hash", NpgsqlDbType.Bytea).Value = issued.Hash;
            command.Parameters.AddWithValue("role", role.ToString());
            command.Parameters.AddWithValue("expires", (object?)expiresAt ?? DBNull.Value);
            await using var reader = await command.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            link = new SessionShareLink(
                id,
                role,
                expiresAt,
                reader.GetDateTime(0),
                reader.GetDateTime(1),
                null);
        }

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
        SessionShareLink result;
        await using (var command = new NpgsqlCommand(sql, connection, transaction))
        {
            command.Parameters.AddWithValue("id", linkId);
            command.Parameters.AddWithValue("session", sessionId);
            command.Parameters.AddWithValue("role", role.ToString());
            command.Parameters.AddWithValue("expires", (object?)expiresAt ?? DBNull.Value);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                throw new KeyNotFoundException();
            result = MapLink(reader);
        }

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

    /// <summary>
    /// True while the session has a direct share or a link that has not expired, whatever the
    /// role and whether or not a licence is active. An expired link opens nothing, so it does not
    /// count; a share that the missing licence makes inert does, because the licence can come
    /// back without anyone looking at this session again.
    /// </summary>
    /// <remarks>A direct share on an ancestor counts too: it opens this session as well (#410).
    /// So does a share on the project of the session or of an ancestor (#409). An ancestor's link
    /// does not, because links are link-only.</remarks>
    public async Task<bool> IsSharedAsync(string sessionId, CancellationToken ct = default)
    {
        await using var command = _db.CreateCommand($"""
            WITH RECURSIVE {AncestorChain}
            SELECT EXISTS (
                    SELECT 1 FROM session_shares JOIN chain ON chain.id = session_shares.session_id)
                OR EXISTS (
                    SELECT 1 FROM project_shares
                    JOIN projects ON projects.id = project_shares.project_id
                    JOIN chain ON chain.project = projects.id AND chain.owner = projects.owner)
                OR EXISTS (
                    SELECT 1 FROM session_share_links
                    WHERE session_id = @session
                      AND (expires_at IS NULL OR expires_at > now()))
            """);
        command.Parameters.AddWithValue("session", sessionId);
        command.Parameters.AddWithValue("maxDepth", SessionDescent.MaxHops);
        return await command.ExecuteScalarAsync(ct) is true;
    }

    /// <summary>
    /// The session and its ancestors, as CTE <c>chain(id, parent, owner, project, depth)</c>. The walk
    /// stays within one owner: a parent id is whatever the creating request named, and a grant
    /// must never flow into a session somebody else hung under a shared one. The bound is
    /// <see cref="SessionDescent.MaxHops"/>, which also ends a corrupt cycle.
    /// </summary>
    private const string AncestorChain = """
        chain(id, parent, owner, project, depth) AS (
            SELECT id, parent_session_id, owner, project_id, 0 FROM sessions WHERE id = @session
            UNION
            SELECT parent.id, parent.parent_session_id, parent.owner, parent.project_id, chain.depth + 1
            FROM chain
            JOIN sessions parent
              ON parent.id = chain.parent
             AND parent.owner = chain.owner
            WHERE chain.depth < @maxDepth
        )
        """;

    /// <summary>
    /// The project shares that name <c>@principal</c>, directly or through one of its IdP groups,
    /// as rows <c>(project_id, role, name, owner)</c>. Group memberships are those of the
    /// principal's last sign-in (<c>user_groups</c>), read on every request, so leaving a group
    /// in the IdP closes the project at the next sign-in. A grant only reaches sessions whose
    /// owner is the project's owner: <c>project_id</c> on a session is whatever the creating
    /// request named, exactly like a parent id.
    /// </summary>
    private const string ProjectGrants = """
        SELECT project_shares.project_id, project_shares.role, projects.name, projects.owner
        FROM project_shares
        JOIN projects ON projects.id = project_shares.project_id
        WHERE (project_shares.subject_type = 'user' AND project_shares.subject = @principal)
           OR (project_shares.subject_type = 'group' AND project_shares.subject IN (
                SELECT group_name FROM user_groups WHERE owner = @principal))
        """;

    /// <summary>
    /// The policy the session's pod is held to: its own blocks plus those of every ancestor.
    /// A child of a shared session is shared with the same people (#410), and a collaborator
    /// could otherwise ask the parent's agent to start a child and use there the servers the
    /// owner blocked on the parent. Blocks only add up; a child cannot lift its parent's.
    /// The owner's sharing dialog still shows and edits the session's own policy only.
    /// </summary>
    public async Task<SessionMcpPolicy?> GetMcpPolicyAsync(
        string sessionId,
        CancellationToken ct = default)
    {
        await using var command = _db.CreateCommand($"""
            WITH RECURSIVE {AncestorChain}
            SELECT policies.blocked_servers, policies.blocked_tools, policies.updated_at
            FROM session_mcp_policies policies
            JOIN chain ON chain.id = policies.session_id
            ORDER BY chain.depth
            """);
        command.Parameters.AddWithValue("session", sessionId);
        command.Parameters.AddWithValue("maxDepth", SessionDescent.MaxHops);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var servers = new List<string>();
        var tools = new List<string>();
        DateTime? updatedAt = null;
        while (await reader.ReadAsync(ct))
        {
            servers.AddRange(JsonSerializer.Deserialize<string[]>(reader.GetString(0)) ?? []);
            tools.AddRange(JsonSerializer.Deserialize<string[]>(reader.GetString(1)) ?? []);
            var updated = reader.GetDateTime(2);
            if (updatedAt is null || updated > updatedAt) updatedAt = updated;
        }
        return updatedAt is { } at
            ? new SessionMcpPolicy(
                servers.Distinct(StringComparer.Ordinal).ToArray(),
                tools.Distinct(StringComparer.Ordinal).ToArray(),
                at)
            : null;
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

    /// <summary>
    /// The session and the principal's direct shares on it and on its ancestors of the same
    /// owner, plus the shares on the projects of those sessions (#409). Null when the principal
    /// neither owns it nor holds any such grant. Read live on every call, so revoking a parent's
    /// or a project's share closes its sessions on the next request, and a session created in a
    /// shared project opens on the first one.
    /// </summary>
    public async Task<StoredSessionAccess?> FindUserAccessAsync(
        string principal,
        string sessionId,
        CancellationToken ct = default)
    {
        const string sql = $"""
            WITH RECURSIVE {AncestorChain},
            grants(role, source, depth, project, project_name) AS (
                SELECT shares.role, shares.session_id, chain.depth, NULL::text, NULL::text
                FROM chain
                JOIN session_shares shares
                  ON shares.session_id = chain.id
                 AND shares.recipient_owner = @principal
                UNION ALL
                SELECT granted.role, chain.id, chain.depth, granted.project_id, granted.name
                FROM chain
                JOIN ({ProjectGrants}) granted
                  ON granted.project_id = chain.project
                 AND granted.owner = chain.owner
            )
            SELECT {SessionColumns}, grants.role, grants.source, grants.depth, grants.project, grants.project_name
            FROM sessions
            LEFT JOIN grants ON true
            WHERE sessions.id = @session
            ORDER BY grants.depth
            """;

        await using var command = _db.CreateCommand(sql);
        command.Parameters.AddWithValue("principal", principal);
        command.Parameters.AddWithValue("session", sessionId);
        command.Parameters.AddWithValue("maxDepth", SessionDescent.MaxHops);
        await using var reader = await command.ExecuteReaderAsync(ct);
        SessionRecord? session = null;
        var grants = new List<ShareGrant>();
        while (await reader.ReadAsync(ct))
        {
            session ??= MapSession(reader);
            if (!reader.IsDBNull(AfterSession))
                grants.Add(ReadGrant(reader, AfterSession));
        }

        if (session is null)
            return null;
        if (!string.Equals(session.Owner, principal, StringComparison.Ordinal) && grants.Count == 0)
            return null;
        return new StoredSessionAccess(session, grants);
    }

    /// <summary>Reads <c>role, source, depth, project, project_name</c>; a row with a project is a
    /// project grant, any other a session share on the session (depth 0) or an ancestor.</summary>
    private static ShareGrant ReadGrant(NpgsqlDataReader reader, int first)
    {
        var depth = reader.GetInt32(first + 2);
        var project = reader.IsDBNull(first + 3) ? null : reader.GetString(first + 3);
        var scope = project is not null
            ? ShareGrantScope.Project
            : depth == 0 ? ShareGrantScope.Session : ShareGrantScope.Ancestor;
        return new ShareGrant(
            ParseRole(reader.GetString(first)),
            scope,
            reader.GetString(first + 1),
            depth,
            project,
            project is null ? null : reader.GetString(first + 4));
    }

    public Task<StoredSessionAccess?> FindTokenAccessAsync(
        string token, CancellationToken ct = default) =>
        FindTokenAccessCoreAsync(token, updateLastUsed: true, ct);

    public Task<StoredSessionAccess?> FindTokenAccessReadOnlyAsync(
        string token, CancellationToken ct = default) =>
        FindTokenAccessCoreAsync(token, updateLastUsed: false, ct);

    private async Task<StoredSessionAccess?> FindTokenAccessCoreAsync(
        string token, bool updateLastUsed, CancellationToken ct)
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
            role = ParseRole(reader.GetString(AfterSession));
            linkId = reader.GetString(AfterSession + 1);
            storedHash = (byte[])reader.GetValue(AfterSession + 2);
        }

        if (!ShareTokens.Matches(token, storedHash))
            return null;

        if (updateLastUsed)
        {
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
        }

        return new StoredSessionAccess(session, role);
    }

    public async Task<IReadOnlyList<ProjectShare>> ListProjectSharesAsync(
        string owner,
        string projectId,
        CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await EnsureProjectOwnerAsync(connection, transaction, owner, projectId, ct);

        var shares = new List<ProjectShare>();
        await using (var command = new NpgsqlCommand("""
            SELECT subject_type, subject, role, created_at, updated_at
            FROM project_shares
            WHERE project_id = @project
            ORDER BY subject_type DESC, subject
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("project", projectId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                shares.Add(MapProjectShare(reader));
        }
        await transaction.CommitAsync(ct);
        return shares;
    }

    /// <summary>
    /// Adds a share on the project, or changes its role. A user must have signed in here, as for a
    /// session share. A group must be one the owner belongs to: the alternative, any group the IdP
    /// has ever reported, would let every user test group names one by one and hand sessions to
    /// groups they have nothing to do with.
    /// </summary>
    public async Task<ProjectShare> UpsertProjectShareAsync(
        string owner,
        string projectId,
        ProjectShareSubjectType subjectType,
        string subject,
        ShareRole role,
        CancellationToken ct = default)
    {
        ValidateRole(role);
        if (!Enum.IsDefined(subjectType))
            throw new ArgumentException("Subject type must be user or group.", nameof(subjectType));
        subject = subject?.Trim() ?? "";
        if (subject.Length == 0)
            throw new ArgumentException("Subject is required.", nameof(subject));

        await using var connection = await _db.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await EnsureProjectOwnerAsync(connection, transaction, owner, projectId, ct);
        if (subjectType == ProjectShareSubjectType.User)
        {
            if (string.Equals(owner, subject, StringComparison.Ordinal))
                throw new ArgumentException("A project owner cannot share with themselves.", nameof(subject));
            await EnsureRecipientAsync(connection, transaction, subject, ct);
        }
        else
        {
            await EnsureOwnGroupAsync(connection, transaction, owner, subject, ct);
        }

        ProjectShare result;
        await using (var command = new NpgsqlCommand("""
            INSERT INTO project_shares (project_id, subject_type, subject, role)
            VALUES (@project, @type, @subject, @role)
            ON CONFLICT (project_id, subject_type, subject) DO UPDATE SET
                role = EXCLUDED.role,
                updated_at = now()
            RETURNING subject_type, subject, role, created_at, updated_at
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("project", projectId);
            command.Parameters.AddWithValue("type", ProjectShareSubjects.Format(subjectType));
            command.Parameters.AddWithValue("subject", subject);
            command.Parameters.AddWithValue("role", role.ToString());
            await using var reader = await command.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            result = MapProjectShare(reader);
        }
        await transaction.CommitAsync(ct);
        return result;
    }

    public async Task DeleteProjectShareAsync(
        string owner,
        string projectId,
        ProjectShareSubjectType subjectType,
        string subject,
        CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await EnsureProjectOwnerAsync(connection, transaction, owner, projectId, ct);

        await using var command = new NpgsqlCommand("""
            DELETE FROM project_shares
            WHERE project_id = @project AND subject_type = @type AND subject = @subject
            """, connection, transaction);
        command.Parameters.AddWithValue("project", projectId);
        command.Parameters.AddWithValue("type", ProjectShareSubjects.Format(subjectType));
        command.Parameters.AddWithValue("subject", subject);
        if (await command.ExecuteNonQueryAsync(ct) == 0)
            throw new KeyNotFoundException();

        await transaction.CommitAsync(ct);
    }

    private static async Task EnsureProjectOwnerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string owner,
        string projectId,
        CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(
            "SELECT 1 FROM projects WHERE id = @project AND owner = @owner",
            connection,
            transaction);
        command.Parameters.AddWithValue("project", projectId);
        command.Parameters.AddWithValue("owner", owner);
        if (await command.ExecuteScalarAsync(ct) is null)
            throw new KeyNotFoundException();
    }

    private static async Task EnsureOwnGroupAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string owner,
        string group,
        CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(
            "SELECT 1 FROM user_groups WHERE owner = @owner AND group_name = @group",
            connection,
            transaction);
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("group", group);
        if (await command.ExecuteScalarAsync(ct) is null)
            throw new ArgumentException("You can share a project only with a group you belong to.", nameof(group));
    }

    private static ProjectShare MapProjectShare(NpgsqlDataReader reader) => new(
        ProjectShareSubjects.TryParse(reader.GetString(0), out var type)
            ? type
            : throw new InvalidOperationException("Stored subject type is invalid."),
        reader.GetString(1),
        ParseRole(reader.GetString(2)),
        reader.GetDateTime(3),
        reader.GetDateTime(4));

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
            throw new UnknownRecipientException(recipient);
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
        sessions.schedule, sessions.agent_session_id, sessions.status,
        sessions.question_pending, sessions.callback_token, sessions.created_at,
        sessions.updated_at, sessions.image, sessions.run_as_root, sessions.cpu,
        sessions.memory, sessions.mcp_config, sessions.repos, sessions.question_open,
        sessions.question_title, sessions.parent_session_id, sessions.agent, sessions.ui_mode,
        sessions.execution_generation, sessions.provider_switch_pending
        """;
    // The first column a query adds after SessionColumns.
    private const int AfterSession = 25;

    private static SessionRecord MapSession(NpgsqlDataReader reader) => new()
    {
        Id = reader.GetString(0),
        Owner = reader.GetString(1),
        Title = reader.GetString(2),
        Mode = Enum.Parse<SessionMode>(reader.GetString(3)),
        RepoUrl = reader.IsDBNull(4) ? null : reader.GetString(4),
        Schedule = reader.IsDBNull(5) ? null : reader.GetString(5),
        // Access checks must tolerate the same missing legacy resume ID as native history reads.
        AgentSessionId = reader.IsDBNull(6) ? "" : reader.GetString(6),
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
        ReposJson = reader.IsDBNull(17) ? null : reader.GetString(17),
        QuestionOpen = reader.GetBoolean(18),
        QuestionTitle = reader.IsDBNull(19) ? null : reader.GetString(19),
        ParentSessionId = reader.IsDBNull(20) ? null : reader.GetString(20),
        Agent = Enum.Parse<AgentKind>(reader.GetString(21)), UiMode = reader.GetString(22),
        ExecutionGeneration = reader.GetInt64(23), ProviderSwitchPending = reader.GetBoolean(24)
    };
}
