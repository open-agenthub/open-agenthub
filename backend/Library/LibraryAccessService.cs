using System.Security.Cryptography;
using System.Text;
using AgentHub.Api.Licensing;

namespace AgentHub.Api.Library;

/// <summary>
/// Resolves which library items (MCP servers, skills) a user can use.
/// Own items are always accessible. Without an enterprise license the org
/// MCP catalog (<c>__org__</c>) is readable by all authenticated users and personal
/// sharing is off. With a license, org and personal shares follow the share matrix.
/// Skills shared by others are only visible while an enterprise license is active.
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

    /// <summary>
    /// Changes whenever <see cref="ListSkillPayloadsAsync"/> would return something different,
    /// computed from the records alone so a pod polling an unchanged library never makes the
    /// store read a single SKILL.md.
    /// </summary>
    Task<string> SkillPayloadsVersionAsync(
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
        var accessible = new Dictionary<string, McpServerRecord>(StringComparer.Ordinal);
        foreach (var record in own)
            accessible[record.Id] = record;

        if (!_license.Enabled)
        {
            foreach (var org in await _mcpServers.ListByOwnerAsync(McpServerRecord.OrgOwner, ct))
                accessible[org.Id] = org;
        }
        else
        {
            var sharedIds = await SharedIdsAsync(LibraryItemTypes.Mcp, owner, ct);
            if (sharedIds.Count > 0)
            {
                foreach (var shared in await _mcpServers.GetManyAsync(sharedIds, ct))
                {
                    if (shared.Owner != owner)
                        accessible[shared.Id] = shared;
                }
            }
        }

        return accessible.Values.OrderBy(s => s.Name, StringComparer.Ordinal).ToList();
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
        var sharedIds = _license.Enabled
            ? await SharedIdsAsync(LibraryItemTypes.Mcp, owner, ct)
            : (IReadOnlyCollection<string>)[];

        var accessible = records
            .Where(r => IsMcpAccessible(r, owner, sharedIds))
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
        var payloads = new List<SkillPayload>();
        foreach (var record in await ResolveSkillWinnersAsync(owner, projectId, ct))
        {
            var content = await _skills.GetContentAsync(record, ct);
            if (!string.IsNullOrEmpty(content))
            {
                var files = await _skills.GetFilesAsync(record.Id, record.Version, ct);
                payloads.Add(new SkillPayload(record.Name, content, files));
            }
        }
        return payloads.OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
    }

    public async Task<string> SkillPayloadsVersionAsync(
        string owner, string? projectId, CancellationToken ct = default)
    {
        // Version moves with every content or file change; UpdatedAt also catches a rename or a
        // description edit. Which record wins a name is part of the input, so sharing or
        // unsharing a skill, or a licence change that hides shared ones, changes it too.
        var text = new StringBuilder();
        foreach (var record in (await ResolveSkillWinnersAsync(owner, projectId, ct))
                     .OrderBy(r => r.Name, StringComparer.Ordinal))
            text.Append(record.Name).Append('\n').Append(record.Id).Append('\n')
                .Append(record.Version).Append('\n').Append(record.UpdatedAt.Ticks).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..32]
            .ToLowerInvariant();
    }

    // Name conflicts: the session project's skill wins over a personal one, and own skills win
    // against shared ones.
    private async Task<IReadOnlyList<SkillRecord>> ResolveSkillWinnersAsync(
        string owner, string? projectId, CancellationToken ct)
    {
        var records = await ListAccessibleSkillsAsync(owner, projectId, ct);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return records
            .OrderBy(r => r.Owner != owner ? 2 : r.ProjectId is null ? 1 : 0)
            .Where(r => seen.Add(r.Name))
            .ToList();
    }

    private bool IsMcpAccessible(
        McpServerRecord record, string owner, IReadOnlyCollection<string> sharedIds)
    {
        if (record.Owner == owner) return true;
        if (!_license.Enabled)
            return record.Owner == McpServerRecord.OrgOwner;
        return sharedIds.Contains(record.Id);
    }

    private async Task<IReadOnlyCollection<string>> SharedIdsAsync(
        string itemType, string owner, CancellationToken ct)
        => _license.Enabled
            ? await _shares.ListAccessibleItemIdsAsync(itemType, owner, ct)
            : [];
}
