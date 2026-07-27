// -----------------------------------------------------------------------------
// Open AgentHub Enterprise Edition — User groups & group roles.
// Part of the Enterprise Edition; NOT covered by the AGPL-3.0 license of the
// open-core. Source-available under the Open AgentHub Enterprise License
// (see ee/LICENSE); a valid subscription is required for production use.
// -----------------------------------------------------------------------------
using Npgsql;

namespace AgentHub.Api.Ee.Identity;

/// <summary>A group as shown in the admin area: name, assigned role, member count.</summary>
public sealed record GroupInfo(string Name, string? Role, int MemberCount);

/// <summary>
/// Persists the user→group memberships read from the OIDC token's groups claim, plus the
/// admin-configured group→role mapping ("admin" | "user"). Memberships are replaced on each
/// login-claims sync so the IdP stays the source of truth; role mappings are managed in the
/// admin UI and survive membership refreshes.
/// </summary>
public sealed class UserGroupStore
{
    public const string AdminRole = "admin";
    public const string UserRole = "user";

    private readonly NpgsqlDataSource _db;

    public UserGroupStore(IConfiguration cfg)
    {
        var cs = cfg.GetConnectionString("Postgres")
                 ?? throw new InvalidOperationException("ConnectionStrings:Postgres is missing.");
        _db = NpgsqlDataSource.Create(cs);
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        const string ddl = """
            CREATE TABLE IF NOT EXISTS user_groups (
                owner      TEXT NOT NULL,
                group_name TEXT NOT NULL,
                updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
                PRIMARY KEY (owner, group_name)
            );
            CREATE INDEX IF NOT EXISTS idx_user_groups_group ON user_groups(group_name);
            CREATE TABLE IF NOT EXISTS group_roles (
                group_name TEXT PRIMARY KEY,
                role       TEXT NOT NULL CHECK (role IN ('admin', 'user')),
                updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
            );
            """;
        await using var cmd = _db.CreateCommand(ddl);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Replaces the owner's memberships with the groups from the current token.</summary>
    public async Task ReplaceGroupsAsync(string owner, IReadOnlyCollection<string> groups, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using (var del = new NpgsqlCommand("DELETE FROM user_groups WHERE owner = @o", conn, tx))
        {
            del.Parameters.AddWithValue("o", owner);
            await del.ExecuteNonQueryAsync(ct);
        }
        foreach (var group in groups.Where(g => !string.IsNullOrWhiteSpace(g)).Distinct(StringComparer.Ordinal))
        {
            await using var ins = new NpgsqlCommand(
                "INSERT INTO user_groups (owner, group_name) VALUES (@o, @g) ON CONFLICT DO NOTHING", conn, tx);
            ins.Parameters.AddWithValue("o", owner);
            ins.Parameters.AddWithValue("g", group.Trim());
            await ins.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<string>> GetGroupsAsync(string owner, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand(
            "SELECT group_name FROM user_groups WHERE owner = @o ORDER BY group_name");
        cmd.Parameters.AddWithValue("o", owner);
        var list = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(r.GetString(0));
        return list;
    }

    /// <summary>All known groups (from memberships and role mappings) for the admin UI.</summary>
    public async Task<IReadOnlyList<GroupInfo>> ListGroupsAsync(CancellationToken ct = default)
    {
        const string sql = """
            SELECT g.group_name, gr.role, COALESCE(m.members, 0)
            FROM (SELECT group_name FROM user_groups
                  UNION SELECT group_name FROM group_roles) g
            LEFT JOIN group_roles gr ON gr.group_name = g.group_name
            LEFT JOIN (SELECT group_name, COUNT(*)::int AS members FROM user_groups GROUP BY group_name) m
                   ON m.group_name = g.group_name
            ORDER BY g.group_name
            """;
        await using var cmd = _db.CreateCommand(sql);
        var list = new List<GroupInfo>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new GroupInfo(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetInt32(2)));
        return list;
    }

    /// <summary>Assigns a role to a group, or clears the mapping with null.</summary>
    public async Task SetGroupRoleAsync(string group, string? role, CancellationToken ct = default)
    {
        if (role is not (null or AdminRole or UserRole))
            throw new ArgumentException($"Unknown role '{role}'.");
        if (role is null)
        {
            await using var del = _db.CreateCommand("DELETE FROM group_roles WHERE group_name = @g");
            del.Parameters.AddWithValue("g", group);
            await del.ExecuteNonQueryAsync(ct);
            return;
        }
        const string sql = """
            INSERT INTO group_roles (group_name, role) VALUES (@g, @r)
            ON CONFLICT (group_name) DO UPDATE SET role = EXCLUDED.role, updated_at = now();
            """;
        await using var cmd = _db.CreateCommand(sql);
        cmd.Parameters.AddWithValue("g", group);
        cmd.Parameters.AddWithValue("r", role);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>True when the owner belongs to a group mapped to the admin role.</summary>
    public async Task<bool> IsInAdminGroupAsync(string owner, CancellationToken ct = default)
    {
        const string sql = """
            SELECT EXISTS (
                SELECT 1 FROM user_groups ug
                JOIN group_roles gr ON gr.group_name = ug.group_name
                WHERE ug.owner = @o AND gr.role = 'admin')
            """;
        await using var cmd = _db.CreateCommand(sql);
        cmd.Parameters.AddWithValue("o", owner);
        return await cmd.ExecuteScalarAsync(ct) is true;
    }

    /// <summary>True when at least one group is mapped to the admin role (disables bootstrap mode).</summary>
    public async Task<bool> AnyAdminGroupAsync(CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand("SELECT EXISTS (SELECT 1 FROM group_roles WHERE role = 'admin')");
        return await cmd.ExecuteScalarAsync(ct) is true;
    }
}
