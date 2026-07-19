using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AgentHub.Api.Persistence;

namespace AgentHub.Api.Licensing;

/// <summary>
/// Reports the current seat count (licensed users) to the license service as a periodic
/// heartbeat. The service answers with a fresh 45-day token, which we store — so reporting
/// is also how the license renews itself. Stop reporting (or lose the subscription) and the
/// stored token expires, disabling enterprise features. Runs only when a license is
/// activated and Ee:License:ServiceUrl is configured.
/// </summary>
public sealed class SeatUsageReporter(
    IEnterpriseLicense license, LicenseStore store, UserDirectory users,
    IHttpClientFactory httpFactory, IConfiguration cfg, ILogger<SeatUsageReporter> log)
    : BackgroundService
{
    private readonly string? _serviceUrl = cfg["Ee:License:ServiceUrl"]?.TrimEnd('/');
    private readonly TimeSpan _interval = TimeSpan.FromHours(cfg.GetValue("Ee:License:ReportIntervalHours", 24));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_serviceUrl))
        {
            log.LogInformation("Seat reporting disabled: Ee:License:ServiceUrl is not set.");
            return;
        }

        // Small startup delay so schema init / license reload has run.
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ReportOnceAsync(stoppingToken); }
            catch (Exception ex) { log.LogWarning(ex, "Seat usage report failed."); }

            try { await Task.Delay(_interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    public async Task ReportOnceAsync(CancellationToken ct)
    {
        var token = await store.GetTokenAsync(ct);
        if (string.IsNullOrWhiteSpace(token))
            return; // no license activated — nothing to report

        // A token without a license id (lid) predates metering; it can't be reported.
        if (license.Status.LicenseId is null)
        {
            log.LogWarning("License token has no license id — re-activate it to enable seat metering.");
            return;
        }

        var seats = await users.CountLicensedAsync(ct);

        var client = httpFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var resp = await client.PostAsJsonAsync($"{_serviceUrl}/api/usage/report", new { seats }, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
        {
            // 403 = subscription no longer active; the token will lapse and gate features off.
            log.LogWarning("Seat report rejected ({Status}) — {Body}", (int)resp.StatusCode, body);
            return;
        }

        if (TryParseRenewedToken(body, out var renewed))
        {
            // Store-then-verify with rollback: the renewed token must pass the
            // signature check against the compiled-in service key. A rogue or
            // broken endpoint behind Ee:License:ServiceUrl can therefore neither
            // unlock features nor clobber a previously valid stored token.
            await store.SetTokenAsync(renewed, ct);
            await license.ReloadAsync(ct);
            if (!license.Status.Valid)
            {
                log.LogWarning("Renewed license token failed verification ({Reason}) — keeping the previous token.",
                    license.Status.Reason);
                await store.SetTokenAsync(token, ct);
                await license.ReloadAsync(ct);
                return;
            }
            await store.SetLastReportAsync(DateTime.UtcNow, ct);
            log.LogInformation("Reported {Seats} seat(s); license token renewed.", seats);
        }
        else
        {
            log.LogWarning("Seat report accepted but no token was returned.");
        }
    }

    /// <summary>Extracts the renewed token from a report response body, if present.</summary>
    public static bool TryParseRenewedToken(string json, out string token)
    {
        token = "";
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("token", out var t) && t.ValueKind == JsonValueKind.String)
            {
                token = t.GetString() ?? "";
                return token.Length > 0;
            }
        }
        catch (JsonException) { /* not JSON / unexpected shape */ }
        return false;
    }
}
