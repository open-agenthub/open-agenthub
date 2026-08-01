// -----------------------------------------------------------------------------
// Open AgentHub Enterprise Edition — Library sharing (MCP catalog).
// Part of the Enterprise Edition; NOT covered by the AGPL-3.0 license of the
// open-core. Source-available under the Open AgentHub Enterprise License
// (see ee/LICENSE); a valid subscription is required for production use.
// -----------------------------------------------------------------------------
using AgentHub.Api.Library;
using Npgsql;

namespace AgentHub.Api.Ee.Library;

/// <summary>
/// Postgres persistence for MCP catalog shares. Group subjects are IdP group
/// names from <c>UserGroupStore</c> (<c>user_groups.group_name</c>), not a
/// parallel custom-group system.
/// </summary>
public sealed class LibraryShareStore : ILibraryShareStore
{
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
            CREATE TABLE IF NOT EXISTS library_shares (
                item_type    TEXT NOT NULL CHECK (item_type IN ('mcp')),
                item_id      TEXT NOT NULL,
                subject_type TEXT NOT NULL CHECK (subject_type IN ('user', 'group', 'all')),
                subject      TEXT NOT NULL DEFAULT '',
                created_by   TEXT NOT NULL,
                created_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
                PRIMARY KEY (item_type, item_id, subject_type, subject)
            );
            CREATE INDEX IF NOT EXISTS idx_library_shares_subject
                ON library_shares(subject_type, subject);
            """;
        await using var cmd = _db.CreateCommand(ddl);
        await cmd.ExecuteNonQueryAsync(ct);
    }

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
                    SELECT group_name FROM user_groups WHERE owner = @owner)))
            """);
        cmd.Parameters.AddWithValue("type", itemType);
        cmd.Parameters.AddWithValue("owner", owner);

        var ids = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            ids.Add(reader.GetString(0));
        return ids;
    }

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
            await using var cmd = new NpgsqlCommand("""
                SELECT 1 WHERE EXISTS (
                    SELECT 1 FROM user_groups WHERE group_name = @g
                    UNION
                    SELECT 1 FROM group_roles WHERE group_name = @g)
                """, connection, transaction);
            cmd.Parameters.AddWithValue("g", group);
            if (await cmd.ExecuteScalarAsync(ct) is null)
                throw new ArgumentException($"Group '{group}' does not exist.");
        }
    }
}
