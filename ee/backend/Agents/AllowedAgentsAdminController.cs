// -----------------------------------------------------------------------------
// Open AgentHub Enterprise Edition — Admin API for allowed agent kinds.
// Part of the Enterprise Edition; NOT covered by the AGPL-3.0 license of the
// open-core. Source-available under the Open AgentHub Enterprise License
// (see ee/LICENSE); a valid subscription is required for production use.
// -----------------------------------------------------------------------------
using System.Security.Claims;
using AgentHub.Api.Admin;
using AgentHub.Api.Licensing;
using AgentHub.Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentHub.Api.Ee.Agents;

/// <summary>
/// Enterprise admin API for the optional agent-kind whitelist. Empty list =
/// no restriction (all agents allowed). Requires a valid license (402) and an
/// admin caller (403).
/// </summary>
[ApiController]
[Authorize]
[Route("api/admin/allowed-agents")]
public sealed class AllowedAgentsAdminController : ControllerBase
{
    private readonly IEnterpriseLicense _license;
    private readonly AdminAccess _access;
    private readonly AllowedAgentsStore _store;

    public AllowedAgentsAdminController(
        IEnterpriseLicense license, AdminAccess access, AllowedAgentsStore store)
    {
        _license = license;
        _access = access;
        _store = store;
    }

    private string Owner =>
        User.FindFirstValue("preferred_username")
        ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? "dev";

    private ObjectResult? LicenseFailure()
        => _license.Enabled
            ? null
            : StatusCode(StatusCodes.Status402PaymentRequired,
                new { error = "An active enterprise license is required." });

    private async Task<IActionResult?> GateAsync(CancellationToken ct)
    {
        if (LicenseFailure() is { } payment) return payment;
        if (!await _access.IsAdminAsync(Owner, ct)) return Forbid();
        return null;
    }

    public sealed record AllowedAgentsResponse(IReadOnlyList<string> Agents);
    public sealed record SetAllowedAgentsReq(IReadOnlyList<string>? Agents);

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        if (await GateAsync(ct) is { } fail) return fail;
        var listed = await _store.ListAsync(ct);
        return Ok(new AllowedAgentsResponse(listed.Select(a => a.ToString()).ToArray()));
    }

    [HttpPut]
    public async Task<IActionResult> Put([FromBody] SetAllowedAgentsReq req, CancellationToken ct)
    {
        if (await GateAsync(ct) is { } fail) return fail;
        var raw = req.Agents ?? Array.Empty<string>();
        var parsed = new List<AgentKind>();
        foreach (var name in raw)
        {
            if (string.IsNullOrWhiteSpace(name)
                || !Enum.TryParse<AgentKind>(name.Trim(), ignoreCase: true, out var kind)
                || !Enum.IsDefined(kind))
            {
                return BadRequest(new { error = $"Unknown agent kind '{name}'." });
            }
            parsed.Add(kind);
        }
        await _store.ReplaceAsync(parsed, ct);
        var listed = await _store.ListAsync(ct);
        return Ok(new AllowedAgentsResponse(listed.Select(a => a.ToString()).ToArray()));
    }
}
