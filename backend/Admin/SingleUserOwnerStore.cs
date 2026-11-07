using AgentHub.Api.Persistence;

namespace AgentHub.Api.Admin;

/// <summary>Data that belongs to an owner from API-key or development mode.</summary>
public sealed record SingleUserOwnerData(string Owner, long Sessions, long Projects);

/// <summary>
/// The owner names an install used without OIDC: the API-key owner and the insecure development
/// user. Ownership is by username, so after a switch to OIDC whatever those names own becomes
/// the property of the identity-provider user who signs in with the same username — on a
/// provider with self-registration, whoever picks it first. AdminAccess uses these names to keep
/// such a user from inheriting admin rights, and the admin area to point at the data
/// (docs/admin-bootstrap.md).
/// </summary>
public interface ISingleUserOwnerStore
{
    /// <summary>Remembers the owner the running mode acts as, so it is still known after a switch.</summary>
    Task RecordAsync(string owner, string mode, CancellationToken ct = default);

    /// <summary>
    /// Every name that may hold data from a mode without OIDC: the recorded ones, the one the chart
    /// carried forward (<c>Auth:FormerApiKeyOwner</c>), and the chart's defaults "admin" and "dev".
    /// </summary>
    Task<IReadOnlyCollection<string>> ListOwnersAsync(CancellationToken ct = default);

    /// <summary>Those owners that own sessions or projects.</summary>
    Task<IReadOnlyList<SingleUserOwnerData>> ListWithDataAsync(CancellationToken ct = default);
}

public sealed class SingleUserOwnerStore(IConfiguration cfg) : ISingleUserOwnerStore
{
    // The chart's defaults are always included, not only when nothing is recorded: an install
    // that switched to OIDC before this table existed recorded nothing, and its key acted as
    // "admin" (fresh install) or "dev" (upgraded from the open mode).
    private static readonly string[] DefaultOwners =
        [Auth.HubAuthOptions.DefaultApiKeyOwner, Auth.HubAuthOptions.InsecureDevUser];

    private readonly Npgsql.NpgsqlDataSource _db = PostgresDataSources.Shared(cfg);

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await SchemaScript.ApplyAsync(_db, nameof(SingleUserOwnerStore), """
            CREATE TABLE IF NOT EXISTS single_user_owners (
                owner     TEXT NOT NULL,
                mode      TEXT NOT NULL,
                last_seen TIMESTAMPTZ NOT NULL DEFAULT now()
            );
            CREATE UNIQUE INDEX IF NOT EXISTS single_user_owners_owner_key ON single_user_owners (lower(owner));
            """, ct);
    }

    public async Task RecordAsync(string owner, string mode, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand("""
            INSERT INTO single_user_owners (owner, mode) VALUES (@o, @m)
            ON CONFLICT (lower(owner)) DO UPDATE SET mode = EXCLUDED.mode, last_seen = now()
            """);
        cmd.Parameters.AddWithValue("o", owner);
        cmd.Parameters.AddWithValue("m", mode);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyCollection<string>> ListOwnersAsync(CancellationToken ct = default)
    {
        var owners = new HashSet<string>(DefaultOwners, StringComparer.OrdinalIgnoreCase);
        var carried = cfg["Auth:FormerApiKeyOwner"]?.Trim();
        if (!string.IsNullOrEmpty(carried)) owners.Add(carried);
        await using var cmd = _db.CreateCommand("SELECT owner FROM single_user_owners");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) owners.Add(r.GetString(0));
        return owners;
    }

    public async Task<IReadOnlyList<SingleUserOwnerData>> ListWithDataAsync(CancellationToken ct = default)
    {
        var owners = await ListOwnersAsync(ct);
        // The stores create their tables independently, and a reference to a missing table fails
        // the whole statement at parse time, so ask first which ones exist.
        bool hasSessions, hasProjects;
        await using (var probe = _db.CreateCommand(
            "SELECT to_regclass('sessions') IS NOT NULL, to_regclass('projects') IS NOT NULL"))
        await using (var pr = await probe.ExecuteReaderAsync(ct))
        {
            await pr.ReadAsync(ct);
            hasSessions = pr.GetBoolean(0);
            hasProjects = pr.GetBoolean(1);
        }
        var sessions = hasSessions ? "(SELECT count(*) FROM sessions s WHERE lower(s.owner) = lower(o.owner))" : "0::bigint";
        var projects = hasProjects ? "(SELECT count(*) FROM projects p WHERE lower(p.owner) = lower(o.owner))" : "0::bigint";
        await using var cmd = _db.CreateCommand(
            $"SELECT o.owner, {sessions}, {projects} FROM unnest(@owners) AS o(owner)");
        cmd.Parameters.AddWithValue("owners", owners.ToArray());
        var result = new List<SingleUserOwnerData>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var data = new SingleUserOwnerData(r.GetString(0), r.GetInt64(1), r.GetInt64(2));
            if (data.Sessions + data.Projects > 0) result.Add(data);
        }
        return result.OrderBy(d => d.Owner, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
