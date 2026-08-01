// -----------------------------------------------------------------------------
// Open AgentHub Enterprise Edition — Admin-managed allowed agent kinds.
// Part of the Enterprise Edition; NOT covered by the AGPL-3.0 license of the
// open-core. Source-available under the Open AgentHub Enterprise License
// (see ee/LICENSE); a valid subscription is required for production use.
// -----------------------------------------------------------------------------
using AgentHub.Api.Models;
using Npgsql;

namespace AgentHub.Api.Ee.Agents;

/// <summary>
/// Persists the optional agent-kind whitelist. An empty table means no restriction
/// (every known agent is allowed); a non-empty table is an exact whitelist.
/// </summary>
public sealed class AllowedAgentsStore
{
    private readonly NpgsqlDataSource? _db;
    private readonly HashSet<string>? _memory;

    public AllowedAgentsStore(IConfiguration cfg)
    {
        var cs = cfg.GetConnectionString("Postgres")
                 ?? throw new InvalidOperationException("ConnectionStrings:Postgres is missing.");
        _db = NpgsqlDataSource.Create(cs);
    }

    /// <summary>In-memory store for unit tests (no Postgres).</summary>
    private AllowedAgentsStore(HashSet<string> memory) => _memory = memory;

    public static AllowedAgentsStore InMemory()
        => new(new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_memory is not null) return;
        const string ddl = """
            CREATE TABLE IF NOT EXISTS allowed_agents (
                agent TEXT PRIMARY KEY
            );
            """;
        await using var cmd = _db!.CreateCommand(ddl);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<AgentKind>> ListAsync(CancellationToken ct = default)
    {
        if (_memory is not null)
        {
            lock (_memory)
            {
                return _memory
                    .Select(ParseAgent)
                    .Where(a => a.HasValue)
                    .Select(a => a!.Value)
                    .OrderBy(a => a.ToString(), StringComparer.Ordinal)
                    .ToArray();
            }
        }

        await using var cmd = _db!.CreateCommand(
            "SELECT agent FROM allowed_agents ORDER BY agent");
        var list = new List<AgentKind>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var parsed = ParseAgent(r.GetString(0));
            if (parsed.HasValue) list.Add(parsed.Value);
        }
        return list;
    }

    public async Task<bool> IsUnrestrictedAsync(CancellationToken ct = default)
        => (await ListAsync(ct)).Count == 0;

    /// <summary>
    /// Replaces the whitelist. An empty set clears the restriction (all agents allowed).
    /// </summary>
    public async Task ReplaceAsync(IEnumerable<AgentKind> agents, CancellationToken ct = default)
    {
        var names = agents
            .Distinct()
            .Select(a => a.ToString())
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        if (_memory is not null)
        {
            lock (_memory)
            {
                _memory.Clear();
                foreach (var n in names) _memory.Add(n);
            }
            return;
        }

        await using var conn = await _db!.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM allowed_agents";
            await del.ExecuteNonQueryAsync(ct);
        }
        foreach (var name in names)
        {
            await using var ins = conn.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = "INSERT INTO allowed_agents (agent) VALUES (@a)";
            ins.Parameters.AddWithValue("a", name);
            await ins.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    private static AgentKind? ParseAgent(string raw)
        => Enum.TryParse<AgentKind>(raw, ignoreCase: true, out var kind) && Enum.IsDefined(kind)
            ? kind
            : null;
}
