using Npgsql;

namespace AgentHub.Api.Admin;

/// <summary>One entry of the role audit trail: who changed which user's or group's role, when.</summary>
public sealed record RoleAuditEntry(
    DateTime At, string Actor, string SubjectKind, string Subject, string? OldRole, string? NewRole);

/// <summary>
/// Per-user roles set in the admin area, plus the audit trail of every role change (per user
/// and, from the Enterprise Edition, per group). Community Edition: promoting or demoting one
/// person is what used to need an <c>Ee:Admins</c> values edit and a redeploy.
/// </summary>
public interface IUserRoleStore
{
    /// <summary>The explicit role set for the owner ("admin" | "user"), or null when none is set.</summary>
    Task<string?> GetRoleAsync(string owner, CancellationToken ct = default);

    /// <summary>All explicit roles, keyed by owner (case-insensitive).</summary>
    Task<IReadOnlyDictionary<string, string>> ListRolesAsync(CancellationToken ct = default);

    /// <summary>Sets the owner's role, or clears it with null; returns the previous role.</summary>
    Task<string?> SetRoleAsync(string owner, string? role, CancellationToken ct = default);

    /// <summary>True when at least one user is explicitly an admin (ends bootstrap mode).</summary>
    Task<bool> AnyAdminAsync(CancellationToken ct = default);

    Task AuditAsync(string actor, string subjectKind, string subject, string? oldRole, string? newRole,
        CancellationToken ct = default);

    Task<IReadOnlyList<RoleAuditEntry>> ListAuditAsync(int limit, CancellationToken ct = default);

    /// <summary>
    /// Serializes role changes across replicas until disposed. Without it, two admins demoting
    /// each other at the same moment would each see the other still an admin, both changes
    /// would pass the last-admin check, and nobody would be left.
    /// </summary>
    Task<IAsyncDisposable> LockAsync(CancellationToken ct = default);
}

public sealed class UserRoleStore : IUserRoleStore
{
    public const string AdminRole = "admin";
    public const string UserRole = "user";

    private readonly NpgsqlDataSource _db;

    public UserRoleStore(IConfiguration cfg)
    {
        var cs = cfg.GetConnectionString("Postgres")
                 ?? throw new InvalidOperationException("ConnectionStrings:Postgres is missing.");
        _db = NpgsqlDataSource.Create(cs);
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        // Owners are matched case-insensitively everywhere else (Ee:Admins), so the key is
        // lower(owner): a role set for "Alice" must not be shadowed by a second row for "alice".
        const string ddl = """
            CREATE TABLE IF NOT EXISTS user_roles (
                owner      TEXT NOT NULL,
                role       TEXT NOT NULL CHECK (role IN ('admin', 'user')),
                updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
            );
            CREATE UNIQUE INDEX IF NOT EXISTS user_roles_owner_key ON user_roles (lower(owner));
            CREATE TABLE IF NOT EXISTS role_audit (
                id           BIGSERIAL PRIMARY KEY,
                at           TIMESTAMPTZ NOT NULL DEFAULT now(),
                actor        TEXT NOT NULL,
                subject_kind TEXT NOT NULL CHECK (subject_kind IN ('user', 'group')),
                subject      TEXT NOT NULL,
                old_role     TEXT,
                new_role     TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_role_audit_at ON role_audit (at DESC);
            """;
        await using var cmd = _db.CreateCommand(ddl);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<string?> GetRoleAsync(string owner, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand("SELECT role FROM user_roles WHERE lower(owner) = lower(@o)");
        cmd.Parameters.AddWithValue("o", owner);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    public async Task<IReadOnlyDictionary<string, string>> ListRolesAsync(CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand("SELECT owner, role FROM user_roles");
        var roles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) roles[r.GetString(0)] = r.GetString(1);
        return roles;
    }

    public async Task<string?> SetRoleAsync(string owner, string? role, CancellationToken ct = default)
    {
        if (role is not (null or AdminRole or UserRole))
            throw new ArgumentException($"Unknown role '{role}'.");

        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        string? previous;
        await using (var get = new NpgsqlCommand(
            "SELECT role FROM user_roles WHERE lower(owner) = lower(@o) FOR UPDATE", conn, tx))
        {
            get.Parameters.AddWithValue("o", owner);
            previous = await get.ExecuteScalarAsync(ct) as string;
        }
        if (role is null)
        {
            await using var del = new NpgsqlCommand("DELETE FROM user_roles WHERE lower(owner) = lower(@o)", conn, tx);
            del.Parameters.AddWithValue("o", owner);
            await del.ExecuteNonQueryAsync(ct);
        }
        else
        {
            await using var upsert = new NpgsqlCommand("""
                INSERT INTO user_roles (owner, role) VALUES (@o, @r)
                ON CONFLICT (lower(owner)) DO UPDATE SET role = EXCLUDED.role, updated_at = now()
                """, conn, tx);
            upsert.Parameters.AddWithValue("o", owner);
            upsert.Parameters.AddWithValue("r", role);
            await upsert.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return previous;
    }

    public async Task<bool> AnyAdminAsync(CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand("SELECT EXISTS (SELECT 1 FROM user_roles WHERE role = 'admin')");
        return await cmd.ExecuteScalarAsync(ct) is true;
    }

    public async Task AuditAsync(string actor, string subjectKind, string subject, string? oldRole, string? newRole,
        CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand("""
            INSERT INTO role_audit (actor, subject_kind, subject, old_role, new_role)
            VALUES (@a, @k, @s, @o, @n)
            """);
        cmd.Parameters.AddWithValue("a", actor);
        cmd.Parameters.AddWithValue("k", subjectKind);
        cmd.Parameters.AddWithValue("s", subject);
        cmd.Parameters.AddWithValue("o", (object?)oldRole ?? DBNull.Value);
        cmd.Parameters.AddWithValue("n", (object?)newRole ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<RoleAuditEntry>> ListAuditAsync(int limit, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand("""
            SELECT at, actor, subject_kind, subject, old_role, new_role
            FROM role_audit ORDER BY at DESC, id DESC LIMIT @l
            """);
        cmd.Parameters.AddWithValue("l", Math.Clamp(limit, 1, 1000));
        var list = new List<RoleAuditEntry>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new RoleAuditEntry(
                r.GetDateTime(0), r.GetString(1), r.GetString(2), r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5)));
        return list;
    }

    // Arbitrary but fixed: every replica has to pick the same advisory-lock key.
    private const long RoleChangeLockKey = 0x726F6C6573; // "roles"

    public async Task<IAsyncDisposable> LockAsync(CancellationToken ct = default)
    {
        var conn = await _db.OpenConnectionAsync(ct);
        try
        {
            await using var cmd = new NpgsqlCommand("SELECT pg_advisory_lock(@k)", conn);
            cmd.Parameters.AddWithValue("k", RoleChangeLockKey);
            await cmd.ExecuteNonQueryAsync(ct);
            return new AdvisoryLock(conn);
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }

    private sealed class AdvisoryLock(NpgsqlConnection conn) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            // Disposing returns the connection to the pool, which resets session state only when
            // the connection is handed out again — until then the lock would stay held and the
            // next role change on another replica would hang. Unlock explicitly.
            try
            {
                await using var cmd = new NpgsqlCommand("SELECT pg_advisory_unlock(@k)", conn);
                cmd.Parameters.AddWithValue("k", RoleChangeLockKey);
                await cmd.ExecuteNonQueryAsync();
            }
            finally { await conn.DisposeAsync(); }
        }
    }
}
