// -----------------------------------------------------------------------------
// Open AgentHub Enterprise Edition — Allowed-agent resolution.
// Part of the Enterprise Edition; NOT covered by the AGPL-3.0 license of the
// open-core. Source-available under the Open AgentHub Enterprise License
// (see ee/LICENSE); a valid subscription is required for production use.
// -----------------------------------------------------------------------------
using AgentHub.Api.Agents;
using AgentHub.Api.Licensing;
using AgentHub.Api.Models;

namespace AgentHub.Api.Ee.Agents;

/// <summary>
/// Enterprise allowed-agent gate. Without a valid license (or with an empty
/// whitelist) every known agent is allowed — restriction is opt-in.
/// </summary>
public sealed class EeAllowedAgentsProvider : IAllowedAgentsProvider
{
    private static readonly IReadOnlyList<AgentKind> All =
        Enum.GetValues<AgentKind>().ToArray();

    private readonly IEnterpriseLicense _license;
    private readonly AllowedAgentsStore _store;

    public EeAllowedAgentsProvider(IEnterpriseLicense license, AllowedAgentsStore store)
    {
        _license = license;
        _store = store;
    }

    public async Task<IReadOnlyList<AgentKind>> GetAllowedAsync(CancellationToken ct = default)
    {
        if (!_license.Enabled) return All;
        var listed = await _store.ListAsync(ct);
        return listed.Count == 0 ? All : listed;
    }

    public async Task<bool> IsAllowedAsync(AgentKind agent, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(agent)) return false;
        var allowed = await GetAllowedAsync(ct);
        return allowed.Contains(agent);
    }
}
