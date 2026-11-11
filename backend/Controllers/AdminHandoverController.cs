using System.Security.Claims;
using AgentHub.Api.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentHub.Api.Controllers;

/// <summary>
/// Admin area: hand the data of the API-key owner to a chosen team-login user (#501,
/// docs/apikey-handover.md). Preview first; apply takes the preview's token, so what is applied is
/// exactly what the admin was shown.
/// </summary>
[ApiController]
[Authorize]
[Route("api/admin/handover")]
public sealed class AdminHandoverController(AdminAccess access, OwnerHandoverService handover) : ControllerBase
{
    private string Owner =>
        User.FindFirstValue("preferred_username") ?? User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "dev";

    [HttpGet]
    public async Task<IActionResult> Overview(CancellationToken ct)
    {
        if (!await access.IsAdminAsync(Owner, ct)) return Forbid();
        var (sources, targets) = await handover.CandidatesAsync(ct);
        return Ok(new
        {
            sources,
            targets = targets.Select(t => new { t.Owner, t.Email, t.DisplayName }),
            history = await handover.HistoryAsync(ct),
        });
    }

    public sealed record PreviewRequest(string? From, string? To, bool IncludeCredentials);

    [HttpPost("preview")]
    public async Task<IActionResult> Preview([FromBody] PreviewRequest req, CancellationToken ct)
    {
        if (!await access.IsAdminAsync(Owner, ct)) return Forbid();
        try { return Ok(await handover.PreviewAsync(Owner, req.From ?? "", req.To ?? "", req.IncludeCredentials, ct)); }
        catch (HandoverRefusedException e) { return Refused(e); }
    }

    public sealed record ApplyRequest(string? Token);

    [HttpPost("apply")]
    public async Task<IActionResult> Apply([FromBody] ApplyRequest req, CancellationToken ct)
    {
        if (!await access.IsAdminAsync(Owner, ct)) return Forbid();
        if (string.IsNullOrWhiteSpace(req.Token)) return BadRequest(new { error = "A preview token is required.", code = "invalid_token" });
        try { return Ok(await handover.ApplyAsync(Owner, req.Token, ct)); }
        catch (HandoverRefusedException e) { return Refused(e); }
    }

    private ObjectResult Refused(HandoverRefusedException e) =>
        StatusCode(e.Status, new { error = e.Message, code = e.Code, sessions = e.Sessions });
}
