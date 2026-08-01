using AgentHub.Api.Ee.Library;
using AgentHub.Api.Licensing;

namespace AgentHub.Api.Library;

/// <summary>
/// Resolves which library items (MCP servers, skills) a user can use.
/// Own items are always accessible. Items shared by others (per user, per
/// group, or with everyone) are only visible while an enterprise license is
/// active — without a license the library is strictly personal.
/// </summary>
public interface ILibraryAccess
{
    Task<IReadOnlyList<McpServerRecord>> ListMcpServersAsync(string owner, CancellationToken ct = default);
    Task<IReadOnlyList<SkillRecord>> ListSkillsAsync(string owner, CancellationToken ct = default);
    Task<SkillRecord?> GetSkillAsync(string owner, string id, CancellationToken ct = default);

    /// <summary>
    /// Resolves saved MCP servers for a session. With <paramref name="strict"/> the
    /// call throws when an id is unknown or inaccessible (create/update validation);
    /// otherwise inaccessible ids are silently dropped (spawn time, e.g. after a
    /// license lapse or a revoked share).
    /// </summary>
    Task<IReadOnlyList<McpServerRecord>> ResolveMcpServersAsync(
        string owner, IReadOnlyCollection<string> ids, bool strict, CancellationToken ct = default);

    /// <summary>
    /// Skills visible to a session: the owner's personal skills, the skills of the
    /// session's project (when set) and shared skills. Skills of other projects
    /// stay out of scope.
    /// </summary>
    Task<IReadOnlyList<SkillRecord>> ListAccessibleSkillsAsync(
        string owner, string? projectId, CancellationToken ct = default);

    /// <summary>Skills materialized into an agent pod, name conflicts resolved
    /// (project beats personal beats shared).</summary>
    Task<IReadOnlyList<SkillPayload>> ListSkillPayloadsAsync(
        string owner, string? projectId, CancellationToken ct = default);
}

public sealed class LibraryAccessService : ILibraryAccess
{
    private readonly IMcpServerStore _mcpServers;
    private readonly ISkillStore _skills;
    private readonly ILibraryShareReader _shares;
    private readonly IEnterpriseLicense _license;

    public LibraryAccessService(
        IMcpServerStore mcpServers,
        ISkillStore skills,
        ILibraryShareReader shares,
        IEnterpriseLicense license)
    {
        _mcpServers = mcpServers;
        _skills = skills;
        _shares = shares;
        _license = license;
    }

    public async Task<IReadOnlyList<McpServerRecord>> ListMcpServersAsync(
        string owner, CancellationToken ct = default)
    {
        var own = await _mcpServers.ListByOwnerAsync(owner, ct);
        var sharedIds = await SharedIdsAsync(LibraryItemTypes.Mcp, owner, ct);
        if (sharedIds.Count == 0) return own;

        var shared = (await _mcpServers.GetManyAsync(sharedIds, ct))
            .Where(s => s.Owner != owner);
        return own.Concat(shared).OrderBy(s => s.Name, StringComparer.Ordinal).ToList();
    }

    public async Task<IReadOnlyList<SkillRecord>> ListSkillsAsync(
        string owner, CancellationToken ct = default)
    {
        var own = await _skills.ListByOwnerAsync(owner, ct);
        var sharedIds = await SharedIdsAsync(LibraryItemTypes.Skill, owner, ct);
        if (sharedIds.Count == 0) return own;

        var shared = (await _skills.GetManyAsync(sharedIds, ct))
            .Where(s => s.Owner != owner);
        return own.Concat(shared).OrderBy(s => s.Name, StringComparer.Ordinal).ToList();
    }

    public async Task<SkillRecord?> GetSkillAsync(
        string owner, string id, CancellationToken ct = default)
    {
        var records = await _skills.GetManyAsync([id], ct);
        var record = records.FirstOrDefault();
        if (record is null) return null;
        if (record.Owner == owner) return record;

        var sharedIds = await SharedIdsAsync(LibraryItemTypes.Skill, owner, ct);
        return sharedIds.Contains(id) ? record : null;
    }

    public async Task<IReadOnlyList<McpServerRecord>> ResolveMcpServersAsync(
        string owner, IReadOnlyCollection<string> ids, bool strict, CancellationToken ct = default)
    {
        var distinct = ids.Where(i => !string.IsNullOrWhiteSpace(i)).Distinct().ToList();
        if (distinct.Count == 0) return [];

        var records = await _mcpServers.GetManyAsync(distinct, ct);
        var sharedIds = await SharedIdsAsync(LibraryItemTypes.Mcp, owner, ct);
        var accessible = records
            .Where(r => r.Owner == owner || sharedIds.Contains(r.Id))
            .ToList();

        if (strict && accessible.Count != distinct.Count)
        {
            var found = accessible.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
            var missing = distinct.Where(i => !found.Contains(i));
            throw new ArgumentException(
                $"Unknown or inaccessible MCP server(s): {string.Join(", ", missing)}.");
        }
        return accessible;
    }

    public async Task<IReadOnlyList<SkillRecord>> ListAccessibleSkillsAsync(
        string owner, string? projectId, CancellationToken ct = default)
    {
        var all = await ListSkillsAsync(owner, ct);
        return all
            .Where(r => r.Owner != owner || r.ProjectId is null || r.ProjectId == projectId)
            .ToList();
    }

    public async Task<IReadOnlyList<SkillPayload>> ListSkillPayloadsAsync(
        string owner, string? projectId, CancellationToken ct = default)
    {
        var records = await ListAccessibleSkillsAsync(owner, projectId, ct);
        var payloads = new List<SkillPayload>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        // Name conflicts: the session project's skill wins over a personal one,
        // and own skills win against shared ones.
        foreach (var record in records.OrderBy(r =>
                     r.Owner != owner ? 2 : r.ProjectId is null ? 1 : 0))
        {
            if (!seen.Add(record.Name)) continue;
            var content = await _skills.GetContentAsync(record, ct);
            if (!string.IsNullOrEmpty(content))
            {
                var files = await _skills.GetFilesAsync(record.Id, record.Version, ct);
                payloads.Add(new SkillPayload(record.Name, content, files));
            }
        }
        return payloads.OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
    }

    private async Task<IReadOnlyCollection<string>> SharedIdsAsync(
        string itemType, string owner, CancellationToken ct)
        => _license.Enabled
            ? await _shares.ListAccessibleItemIdsAsync(itemType, owner, ct)
            : [];
}
