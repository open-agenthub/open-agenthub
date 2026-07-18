using System.Security.Claims;
using AgentHub.Api.Admin;
using AgentHub.Api.Licensing;
using AgentHub.Api.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentHub.Api.Controllers;

/// <summary>
/// Admin area: activate the enterprise license (stored in the DB, not the chart),
/// review seat usage and grant/revoke seats. Billing (Stripe) is handled out of band
/// by the license service; a portal link is surfaced when configured.
/// </summary>
[ApiController]
[Authorize]
[Route("api/admin")]
public sealed class AdminController : ControllerBase
{
    private readonly AdminAccess _access;
    private readonly IEnterpriseLicense _license;
    private readonly LicenseStore _store;
    private readonly UserDirectory _dir;
    private readonly IConfiguration _cfg;

    public AdminController(AdminAccess access, IEnterpriseLicense license, LicenseStore store,
        UserDirectory dir, IConfiguration cfg)
    { _access = access; _license = license; _store = store; _dir = dir; _cfg = cfg; }

    private string Owner =>
        User.FindFirstValue("preferred_username") ?? User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "dev";
    private bool IsAdmin => _access.IsAdmin(Owner);

    /// <summary>Whether the current user may see the admin area (drives the nav item).</summary>
    [HttpGet("access")]
    public object Access() => new { isAdmin = IsAdmin };

    public sealed record SeatInfo(int Used, int Included);
    public sealed record Overview(
        bool IsAdmin, LicenseStatus License, SeatInfo Seats,
        IReadOnlyList<AdminUser> Users, string? BillingPortalUrl, DateTime? LastCheckIn);

    [HttpGet("overview")]
    public async Task<IActionResult> GetOverview(CancellationToken ct)
    {
        if (!IsAdmin) return Forbid();
        var status = _license.Status;
        var used = await _dir.CountLicensedAsync(ct);
        var users = await _dir.ListAsync(ct);
        var lastCheckIn = await _store.GetLastReportAsync(ct);
        return Ok(new Overview(
            true, status, new SeatInfo(used, status.Seats), users, _cfg["Ee:BillingPortalUrl"], lastCheckIn));
    }

    public sealed record CheckoutReq(string Email, string Org, int Seats, string? ReturnUrl);

    /// <summary>
    /// Starts a Stripe checkout on the license service. The service redirects
    /// back to <c>returnUrl</c> with <c>?license=&lt;token&gt;</c> after payment,
    /// which the UI feeds into <see cref="Activate"/>.
    /// </summary>
    [HttpPost("license/checkout")]
    public async Task<IActionResult> StartCheckout([FromBody] CheckoutReq req,
        [FromServices] IHttpClientFactory httpFactory, CancellationToken ct)
    {
        if (!IsAdmin) return Forbid();
        var serviceUrl = _cfg["Ee:License:ServiceUrl"]?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(serviceUrl))
            return StatusCode(StatusCodes.Status501NotImplemented,
                new { error = "No license service configured (Ee:License:ServiceUrl)." });
        if (string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Org) || req.Seats < 1)
            return BadRequest(new { error = "email, org and seats (>= 1) are required." });

        // The redirect target must point back at this instance: same scheme+host
        // as the current request, so a tampered client cannot bounce the license
        // token to a third party.
        var origin = $"{Request.Scheme}://{Request.Host}";
        if (!string.IsNullOrWhiteSpace(req.ReturnUrl) && !IsSameOrigin(req.ReturnUrl, origin))
            return BadRequest(new { error = "returnUrl must be on this instance." });
        var returnUrl = req.ReturnUrl ?? $"{origin}/license/activate";

        using var client = httpFactory.CreateClient();
        using var resp = await client.PostAsJsonAsync($"{serviceUrl}/api/checkout",
            new { email = req.Email.Trim(), org = req.Org.Trim(), seats = req.Seats, returnUrl }, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        return new ContentResult { Content = body, ContentType = "application/json", StatusCode = (int)resp.StatusCode };
    }

    /// <summary>Whether a checkout returnUrl points back at this instance (scheme + authority match).</summary>
    public static bool IsSameOrigin(string returnUrl, string origin)
        => Uri.TryCreate(returnUrl, UriKind.Absolute, out var parsed)
           && string.Equals($"{parsed.Scheme}://{parsed.Authority}", origin, StringComparison.OrdinalIgnoreCase);

    public sealed record ActivateReq(string Token);

    [HttpPost("license")]
    public async Task<IActionResult> Activate([FromBody] ActivateReq req, CancellationToken ct)
    {
        if (!IsAdmin) return Forbid();
        if (string.IsNullOrWhiteSpace(req.Token)) return BadRequest(new { error = "Token is required." });

        // Store then verify: ReloadAsync reads back from the store and validates the
        // signature/expiry. If it doesn't come out valid, roll the store back.
        var previous = await _store.GetTokenAsync(ct);
        await _store.SetTokenAsync(req.Token.Trim(), ct);
        await _license.ReloadAsync(ct);
        var status = _license.Status;
        if (!status.Valid)
        {
            await _store.SetTokenAsync(previous, ct);
            await _license.ReloadAsync(ct);
            return BadRequest(new { error = string.IsNullOrEmpty(status.Reason) ? "License token is invalid." : status.Reason });
        }
        return Ok(status);
    }

    [HttpDelete("license")]
    public async Task<IActionResult> Deactivate(CancellationToken ct)
    {
        if (!IsAdmin) return Forbid();
        await _store.SetTokenAsync(null, ct);
        await _license.ReloadAsync(ct);
        return NoContent();
    }

    public sealed record SeatReq(bool Licensed);

    [HttpPut("users/{owner}/license")]
    public async Task<IActionResult> SetSeat(string owner, [FromBody] SeatReq req, CancellationToken ct)
    {
        if (!IsAdmin) return Forbid();
        var ok = await _dir.SetLicensedAsync(owner, req.Licensed, ct);
        return ok ? NoContent() : NotFound();
    }
}
