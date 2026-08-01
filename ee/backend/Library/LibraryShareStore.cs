// -----------------------------------------------------------------------------
// Open AgentHub Enterprise Edition — Library sharing (MCP servers & skills).
// Part of the Enterprise Edition; NOT covered by the AGPL-3.0 license of the
// open-core. Source-available under the Open AgentHub Enterprise License
// (see ee/LICENSE); a valid subscription is required for production use.
// -----------------------------------------------------------------------------
using Npgsql;

namespace AgentHub.Api.Ee.Library;

/// <summary>
/// Postgres persistence for user groups, library item shares (MCP servers and
/// skills) and the library-wide settings toggle.
/// </summary>
public sealed class LibraryShareStore : ILibraryShareStore
{
    private const string UserSkillPublishingKey = "user_skill_publishing";

    private readonly NpgsqlDataSource _db;

    public LibraryShareStore(IConfiguration cfg)
    {
        var cs = cfg.GetConnectionString("Postgres")
                 ?? throw new InvalidOperationException("ConnectionStrings:Postgres is missing.");
        _db = NpgsqlDataSource.Create(cs);
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        const string ddl = """
            CREATE TABLE IF NOT EXISTS user_groups (
                id         TEXT PRIMARY KEY,
                name       TEXT NOT NULL UNIQUE,
                created_at TIMESTAMPTZ NOT NULL DEFAULT now()
            );
            CREATE TABLE IF NOT EXISTS user_group_members (
                group_id     TEXT NOT NULL,
                member_owner TEXT NOT NULL,
                created_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
                PRIMARY KEY (group_id, member_owner)
            );
            CREATE INDEX IF NOT EXISTS idx_user_group_members_owner
                ON user_group_members(member_owner);

            CREATE TABLE IF NOT EXISTS library_shares (
                item_type    TEXT NOT NULL CHECK (item_type IN ('mcp', 'skill')),
                item_id      TEXT NOT NULL,
                subject_type TEXT NOT NULL CHECK (subject_type IN ('user', 'group', 'all')),
                subject      TEXT NOT NULL DEFAULT '',
                created_by   TEXT NOT NULL,
                created_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
                PRIMARY KEY (item_type, item_id, subject_type, subject)
            );
            CREATE INDEX IF NOT EXISTS idx_library_shares_subject
                ON library_shares(subject_type, subject);

            CREATE TABLE IF NOT EXISTS library_settings (
                key        TEXT PRIMARY KEY,
                value      TEXT NOT NULL,
                updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
            );
            """;
        await using var cmd = _db.CreateCommand(ddl);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ---------------------------------------------------------------- Groups

    public async Task<IReadOnlyList<UserGroup>> ListGroupsAsync(CancellationToken ct = default)
    {
        var groups = new List<(string Id, string Name, DateTime CreatedAt)>();
        await using (var cmd = _db.CreateCommand(
            "SELECT id, name, created_at FROM user_groups ORDER BY name"))
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                groups.Add((reader.GetString(0), reader.GetString(1), reader.GetDateTime(2)));
        }

        var members = new Dictionary<string, List<string>>();
        await using (var cmd = _db.CreateCommand(
            "SELECT group_id, member_owner FROM user_group_members ORDER BY member_owner"))
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var groupId = reader.GetString(0);
                if (!members.TryGetValue(groupId, out var list))
                    members[groupId] = list = new List<string>();
                list.Add(reader.GetString(1));
            }
        }

        return groups
            .Select(g => new UserGroup(
                g.Id, g.Name, members.GetValueOrDefault(g.Id) ?? [], g.CreatedAt))
            .ToList();
    }

    public async Task<UserGroup> CreateGroupAsync(string name, CancellationToken ct = default)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length is 0 or > 100)
            throw new ArgumentException("Group name must be 1-100 characters.");

        var id = Guid.NewGuid().ToString("n")[..12];
        const string sql = """
            INSERT INTO user_groups (id, name) VALUES (@id, @name)
            RETURNING created_at
            """;
        await using var cmd = _db.CreateCommand(sql);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("name", trimmed);
        try
        {
            var createdAt = (DateTime)(await cmd.ExecuteScalarAsync(ct))!;
            return new UserGroup(id, trimmed, [], createdAt);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new ArgumentException("A group with this name already exists.");
        }
    }

    public async Task DeleteGroupAsync(string id, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        await using (var members = new NpgsqlCommand(
            "DELETE FROM user_group_members WHERE group_id = @id", connection, transaction))
        {
            members.Parameters.AddWithValue("id", id);
            await members.ExecuteNonQueryAsync(ct);
        }
        await using (var shares = new NpgsqlCommand(
            "DELETE FROM library_shares WHERE subject_type = 'group' AND subject = @id",
            connection, transaction))
        {
            shares.Parameters.AddWithValue("id", id);
            await shares.ExecuteNonQueryAsync(ct);
        }
        await using (var group = new NpgsqlCommand(
            "DELETE FROM user_groups WHERE id = @id", connection, transaction))
        {
            group.Parameters.AddWithValue("id", id);
            if (await group.ExecuteNonQueryAsync(ct) == 0)
                throw new KeyNotFoundException();
        }

        await transaction.CommitAsync(ct);
    }

    public async Task<UserGroup> SetGroupMembersAsync(
        string id, IReadOnlyCollection<string> members, CancellationToken ct = default)
    {
        var normalized = NormalizeSubjects(members);

        await using var connection = await _db.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        string name;
        DateTime createdAt;
        await using (var group = new NpgsqlCommand(
            "SELECT name, created_at FROM user_groups WHERE id = @id", connection, transaction))
        {
            group.Parameters.AddWithValue("id", id);
            await using var reader = await group.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                throw new KeyNotFoundException();
            name = reader.GetString(0);
            createdAt = reader.GetDateTime(1);
        }

        await EnsureUsersExistAsync(connection, transaction, normalized, ct);

        await using (var clear = new NpgsqlCommand(
            "DELETE FROM user_group_members WHERE group_id = @id", connection, transaction))
        {
            clear.Parameters.AddWithValue("id", id);
            await clear.ExecuteNonQueryAsync(ct);
        }
        foreach (var member in normalized)
        {
            await using var insert = new NpgsqlCommand(
                "INSERT INTO user_group_members (group_id, member_owner) VALUES (@id, @member)",
                connection, transaction);
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.AddWithValue("member", member);
            await insert.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return new UserGroup(id, name, normalized, createdAt);
    }

    // ---------------------------------------------------------------- Shares

    public async Task<LibraryShares> GetSharesAsync(
        string itemType, string itemId, CancellationToken ct = default)
    {
        LibraryItemTypes.Validate(itemType);
        await using var cmd = _db.CreateCommand("""
            SELECT subject_type, subject FROM library_shares
            WHERE item_type = @type AND item_id = @id
            ORDER BY subject
            """);
        cmd.Parameters.AddWithValue("type", itemType);
        cmd.Parameters.AddWithValue("id", itemId);

        var all = false;
        var users = new List<string>();
        var groups = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            switch (reader.GetString(0))
            {
                case "all": all = true; break;
                case "user": users.Add(reader.GetString(1)); break;
                case "group": groups.Add(reader.GetString(1)); break;
            }
        }
        return new LibraryShares(all, users, groups);
    }

    /// <summary>Replaces the full sharing state of one item.</summary>
    public async Task<LibraryShares> SetSharesAsync(
        string itemType,
        string itemId,
        bool all,
        IReadOnlyCollection<string>? users,
        IReadOnlyCollection<string>? groups,
        string createdBy,
        CancellationToken ct = default)
    {
        LibraryItemTypes.Validate(itemType);
        var normalizedUsers = NormalizeSubjects(users ?? []);
        var normalizedGroups = NormalizeSubjects(groups ?? []);

        await using var connection = await _db.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        await EnsureUsersExistAsync(connection, transaction, normalizedUsers, ct);
        await EnsureGroupsExistAsync(connection, transaction, normalizedGroups, ct);

        await using (var clear = new NpgsqlCommand(
            "DELETE FROM library_shares WHERE item_type = @type AND item_id = @id",
            connection, transaction))
        {
            clear.Parameters.AddWithValue("type", itemType);
            clear.Parameters.AddWithValue("id", itemId);
            await clear.ExecuteNonQueryAsync(ct);
        }

        async Task InsertAsync(string subjectType, string subject)
        {
            await using var insert = new NpgsqlCommand("""
                INSERT INTO library_shares (item_type, item_id, subject_type, subject, created_by)
                VALUES (@type, @id, @subjectType, @subject, @createdBy)
                """, connection, transaction);
            insert.Parameters.AddWithValue("type", itemType);
            insert.Parameters.AddWithValue("id", itemId);
            insert.Parameters.AddWithValue("subjectType", subjectType);
            insert.Parameters.AddWithValue("subject", subject);
            insert.Parameters.AddWithValue("createdBy", createdBy);
            await insert.ExecuteNonQueryAsync(ct);
        }

        if (all) await InsertAsync("all", "");
        foreach (var user in normalizedUsers) await InsertAsync("user", user);
        foreach (var group in normalizedGroups) await InsertAsync("group", group);

        await transaction.CommitAsync(ct);
        return new LibraryShares(all, normalizedUsers, normalizedGroups);
    }

    public async Task DeleteForItemAsync(
        string itemType, string itemId, CancellationToken ct = default)
    {
        LibraryItemTypes.Validate(itemType);
        await using var cmd = _db.CreateCommand(
            "DELETE FROM library_shares WHERE item_type = @type AND item_id = @id");
        cmd.Parameters.AddWithValue("type", itemType);
        cmd.Parameters.AddWithValue("id", itemId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyCollection<string>> ListAccessibleItemIdsAsync(
        string itemType, string owner, CancellationToken ct = default)
    {
        LibraryItemTypes.Validate(itemType);
        await using var cmd = _db.CreateCommand("""
            SELECT DISTINCT item_id FROM library_shares
            WHERE item_type = @type AND (
                subject_type = 'all'
                OR (subject_type = 'user' AND subject = @owner)
                OR (subject_type = 'group' AND subject IN (
                    SELECT group_id FROM user_group_members WHERE member_owner = @owner)))
            """);
        cmd.Parameters.AddWithValue("type", itemType);
        cmd.Parameters.AddWithValue("owner", owner);

        var ids = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            ids.Add(reader.GetString(0));
        return ids;
    }

    // ---------------------------------------------------------------- Settings

    public async Task<bool> GetUserSkillPublishingAsync(CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand(
            "SELECT value FROM library_settings WHERE key = @key");
        cmd.Parameters.AddWithValue("key", UserSkillPublishingKey);
        return await cmd.ExecuteScalarAsync(ct) is string value
            && string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    public async Task SetUserSkillPublishingAsync(bool enabled, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand("""
            INSERT INTO library_settings (key, value) VALUES (@key, @value)
            ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, updated_at = now()
            """);
        cmd.Parameters.AddWithValue("key", UserSkillPublishingKey);
        cmd.Parameters.AddWithValue("value", enabled ? "true" : "false");
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ---------------------------------------------------------------- Helpers

    private static List<string> NormalizeSubjects(IReadOnlyCollection<string> values)
    {
        var result = new List<string>();
        foreach (var value in values)
        {
            var subject = value?.Trim();
            if (string.IsNullOrEmpty(subject))
                throw new ArgumentException("Empty entries are not allowed.");
            if (!result.Contains(subject, StringComparer.Ordinal))
                result.Add(subject);
        }
        return result;
    }

    private static async Task EnsureUsersExistAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyCollection<string> users,
        CancellationToken ct)
    {
        foreach (var user in users)
        {
            await using var cmd = new NpgsqlCommand(
                "SELECT 1 FROM app_users WHERE owner = @owner", connection, transaction);
            cmd.Parameters.AddWithValue("owner", user);
            if (await cmd.ExecuteScalarAsync(ct) is null)
                throw new ArgumentException($"'{user}' is not a known user.");
        }
    }

    private static async Task EnsureGroupsExistAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyCollection<string> groups,
        CancellationToken ct)
    {
        foreach (var group in groups)
        {
            await using var cmd = new NpgsqlCommand(
                "SELECT 1 FROM user_groups WHERE id = @id", connection, transaction);
            cmd.Parameters.AddWithValue("id", group);
            if (await cmd.ExecuteScalarAsync(ct) is null)
                throw new ArgumentException($"Group '{group}' does not exist.");
        }
    }
}
