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
    private Task<bool> IsAdminAsync(CancellationToken ct) => _access.IsAdminAsync(Owner, ct);

    /// <summary>Whether the current user may see the admin area (drives the nav item).</summary>
    [HttpGet("access")]
    public async Task<object> Access(CancellationToken ct) => new { isAdmin = await IsAdminAsync(ct) };

    public sealed record SeatInfo(int Used, int Included);
    public sealed record Overview(
        bool IsAdmin, LicenseStatus License, SeatInfo Seats,
        IReadOnlyList<AdminUser> Users, string? BillingPortalUrl, DateTime? LastCheckIn,
        DateTime? CancelAt);

    [HttpGet("overview")]
    public async Task<IActionResult> GetOverview(CancellationToken ct)
    {
        if (!await IsAdminAsync(ct)) return Forbid();
        var status = _license.Status;
        // Claims are cached per process: right after an activation handled by another
        // replica this one would still say "unlicensed". Before claiming that on the
        // admin page, re-read the store once (cheap, admin-only traffic).
        if (!status.Valid)
        {
            await _license.ReloadAsync(ct);
            status = _license.Status;
        }
        var used = await _dir.CountLicensedAsync(ct);
        var users = await _dir.ListAsync(ct);
        var lastCheckIn = await _store.GetLastReportAsync(ct);
        var cancelAt = await _store.GetCancelAtAsync(ct);
        return Ok(new Overview(
            true, status, new SeatInfo(used, status.Seats), users, _cfg["Ee:BillingPortalUrl"], lastCheckIn,
            cancelAt));
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
        if (!await IsAdminAsync(ct)) return Forbid();
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

        // Send this instance's stable key so the resulting license is bound to us: if the
        // redirect and email are both lost, we can still self-activate via license/claim.
        var instanceKey = await _store.GetOrCreateInstanceKeyAsync(ct);

        using var client = httpFactory.CreateClient();
        using var resp = await client.PostAsJsonAsync($"{serviceUrl}/api/checkout",
            new { email = req.Email.Trim(), org = req.Org.Trim(), seats = req.Seats, returnUrl, instanceKey }, ct);
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
        if (!await IsAdminAsync(ct)) return Forbid();
        if (string.IsNullOrWhiteSpace(req.Token)) return BadRequest(new { error = "Token is required." });
        return await ApplyTokenAsync(req.Token.Trim(), "License token is invalid.", ct);
    }

    /// <summary>
    /// Opens the Stripe billing portal for this instance's license: asks the license
    /// service for a fresh portal session using the licensed billing email. A static
    /// Ee:BillingPortalUrl (chart value) takes precedence in the overview; this endpoint
    /// makes the portal work with zero configuration once a license is active.
    /// </summary>
    [HttpPost("billing-portal")]
    public async Task<IActionResult> OpenBillingPortal(
        [FromServices] IHttpClientFactory httpFactory,
        [FromServices] AgentHub.Api.Licensing.ILicenseStore licenseStore,
        CancellationToken ct)
    {
        if (!await IsAdminAsync(ct)) return Forbid();
        var serviceUrl = _cfg["Ee:License:ServiceUrl"]?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(serviceUrl))
            return StatusCode(StatusCodes.Status501NotImplemented,
                new { error = "No license service configured (Ee:License:ServiceUrl)." });

        if (!_license.Status.Valid)
            return BadRequest(new { error = "No active license — activate one to manage billing." });

        // Send the license token, not just the billing address: the service only hands the
        // portal URL back to a caller that can prove the subscription is theirs, and an email
        // address is not proof. Without the token it would only mail the link to the customer.
        var licenseToken = await licenseStore.GetTokenAsync(ct);
        using var client = httpFactory.CreateClient();
        using var resp = await client.PostAsJsonAsync($"{serviceUrl}/api/portal",
            new { licenseToken, email = _license.Status.Email }, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        return new ContentResult { Content = body, ContentType = "application/json", StatusCode = (int)resp.StatusCode };
    }

    public sealed record ClaimReq(string Email);
    private sealed record ClaimResponse(string? Token);

    /// <summary>
    /// Self-service activation: ask the license service for this instance's token using the
    /// billing email plus our stored instance key, then activate it. Recovers a purchase
    /// whose post-checkout redirect and license email were both lost.
    /// </summary>
    [HttpPost("license/claim")]
    public async Task<IActionResult> ClaimLicense([FromBody] ClaimReq req,
        [FromServices] IHttpClientFactory httpFactory, CancellationToken ct)
    {
        if (!await IsAdminAsync(ct)) return Forbid();
        var serviceUrl = _cfg["Ee:License:ServiceUrl"]?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(serviceUrl))
            return StatusCode(StatusCodes.Status501NotImplemented,
                new { error = "No license service configured (Ee:License:ServiceUrl)." });
        if (string.IsNullOrWhiteSpace(req.Email))
            return BadRequest(new { error = "email is required." });

        var instanceKey = await _store.GetOrCreateInstanceKeyAsync(ct);

        using var client = httpFactory.CreateClient();
        using var resp = await client.PostAsJsonAsync($"{serviceUrl}/api/license/claim",
            new { email = req.Email.Trim(), instanceKey }, ct);
        if (!resp.IsSuccessStatusCode)
        {
            // Surface the service's own message (e.g. "no license found", "not active yet").
            var err = await resp.Content.ReadAsStringAsync(ct);
            return new ContentResult { Content = err, ContentType = "application/json", StatusCode = (int)resp.StatusCode };
        }

        var payload = await resp.Content.ReadFromJsonAsync<ClaimResponse>(ct);
        if (payload is null || string.IsNullOrWhiteSpace(payload.Token))
            return BadRequest(new { error = "The license service did not return a token." });

        return await ApplyTokenAsync(payload.Token.Trim(), "Claimed token is invalid.", ct);
    }

    // Store the token, reload + verify against the compiled-in service key, and roll back to
    // the previous token if it doesn't come out valid. Shared by manual activation and claim.
    private async Task<IActionResult> ApplyTokenAsync(string token, string invalidMessage, CancellationToken ct)
    {
        var previous = await _store.GetTokenAsync(ct);
        await _store.SetTokenAsync(token, ct);
        await _license.ReloadAsync(ct);
        var status = _license.Status;
        if (!status.Valid)
        {
            await _store.SetTokenAsync(previous, ct);
            await _license.ReloadAsync(ct);
            return BadRequest(new { error = string.IsNullOrEmpty(status.Reason) ? invalidMessage : status.Reason });
        }
        return Ok(status);
    }

    [HttpDelete("license")]
    public async Task<IActionResult> Deactivate(CancellationToken ct)
    {
        if (!await IsAdminAsync(ct)) return Forbid();
        await _store.SetTokenAsync(null, ct);
        await _license.ReloadAsync(ct);
        return NoContent();
    }

    public sealed record SeatReq(bool Licensed);

    [HttpPut("users/{owner}/license")]
    public async Task<IActionResult> SetSeat(string owner, [FromBody] SeatReq req, CancellationToken ct)
    {
        if (!await IsAdminAsync(ct)) return Forbid();
        var ok = await _dir.SetLicensedAsync(owner, req.Licensed, ct);
        return ok ? NoContent() : NotFound();
    }
}
