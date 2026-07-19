using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.RegularExpressions;
using AgentHub.Api.Chat;
using AgentHub.Api.Chat.Signal;
using AgentHub.Api.Chat.Telegram;
using AgentHub.Api.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentHub.Api.Controllers;

/// <summary>
/// Per-user chat integration settings for the community platforms (Telegram/Signal):
/// link status, notification opt-in/out, and one-shot link codes.
/// </summary>
[ApiController]
[Authorize]
[Route("api/chat")]
public sealed partial class ChatController : ControllerBase
{
    private readonly TelegramOptions _telegramOpts;
    private readonly TelegramClient _telegram;
    private readonly SignalOptions _signalOpts;
    private readonly SignalClient _signal;
    private readonly ChatLinkCodeStore _codes;
    private readonly UserDirectory _dir;

    public ChatController(TelegramOptions telegramOpts, TelegramClient telegram,
        SignalOptions signalOpts, SignalClient signal,
        ChatLinkCodeStore codes, UserDirectory dir)
    {
        _telegramOpts = telegramOpts; _telegram = telegram;
        _signalOpts = signalOpts; _signal = signal;
        _codes = codes; _dir = dir;
    }

    private string Owner =>
        User.FindFirstValue("preferred_username") ?? User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "dev";

    public sealed record TelegramMe(bool Configured, bool Linked, bool Forum, bool Enabled);
    public sealed record SignalMe(bool Configured, string? Number, bool Verified, bool Enabled);
    public sealed record ChatMe(TelegramMe Telegram, SignalMe Signal);
    public sealed record TelegramPrefs(bool Enabled);
    public sealed record SignalPrefs(bool Enabled, string? Number);
    public sealed record SignalVerify(string Code);

    [HttpGet("me")]
    public async Task<ChatMe> Me(CancellationToken ct)
    {
        var user = await _dir.GetAsync(Owner, ct);
        var telegram = new TelegramMe(
            _telegramOpts.Enabled,
            user?.TelegramChatId is not null,
            user?.TelegramForum ?? false,
            user?.TelegramEnabled ?? true);
        var signal = new SignalMe(
            _signalOpts.Enabled,
            user?.SignalNumber,
            user?.SignalVerified ?? false,
            user?.SignalEnabled ?? true);
        return new ChatMe(telegram, signal);
    }

    /// <summary>Mints a one-shot Telegram link code plus the bot's t.me deep link.</summary>
    [HttpPost("telegram/link-code")]
    public async Task<IActionResult> CreateTelegramLinkCode(CancellationToken ct)
    {
        if (!_telegramOpts.CanRun)
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { error = "Telegram integration is not configured on this server." });

        var code = await _codes.CreateAsync(Owner, "telegram", ct: ct);
        var botUsername = await _telegram.GetBotUsernameAsync(ct);
        return Ok(new
        {
            code,
            botUsername,
            deepLink = botUsername is null ? null : $"https://t.me/{botUsername}?start={code}"
        });
    }

    [HttpPut("telegram")]
    public async Task<IActionResult> UpdateTelegram([FromBody] TelegramPrefs prefs, CancellationToken ct)
    {
        await _dir.SetTelegramEnabledAsync(Owner, prefs.Enabled, ct);
        return NoContent();
    }

    /// <summary>Unlinks the user's Telegram chat (notification prefs stay untouched).</summary>
    [HttpDelete("telegram")]
    public async Task<IActionResult> UnlinkTelegram(CancellationToken ct)
    {
        await _dir.ClearTelegramLinkAsync(Owner, ct);
        return NoContent();
    }

    /// <summary>E.164: "+", a non-zero digit, then 6–14 more digits. \z (not $) so a trailing
    /// newline cannot sneak through.</summary>
    [GeneratedRegex(@"^\+[1-9][0-9]{6,14}\z")]
    private static partial Regex E164();

    /// <summary>
    /// Saves Signal prefs. Setting a new number sends a verification code to it via Signal and
    /// answers 202 — the user proves ownership through POST signal/verify. Unchanged/absent
    /// number → 204. SECURITY: phone numbers are PII — log owners, never numbers.
    /// </summary>
    [HttpPut("signal")]
    public async Task<IActionResult> UpdateSignal([FromBody] SignalPrefs prefs, CancellationToken ct)
    {
        var owner = Owner;
        await _dir.SetSignalEnabledAsync(owner, prefs.Enabled, ct);

        if (string.IsNullOrWhiteSpace(prefs.Number)) return NoContent();

        var number = prefs.Number.Replace(" ", "").Replace("-", "");
        if (!E164().IsMatch(number))
            return BadRequest(new { error = "Invalid phone number. Use international format, e.g. +15551234567." });

        var user = await _dir.GetAsync(owner, ct);
        if (number == user?.SignalNumber) return NoContent();

        if (!_signalOpts.CanRun)
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { error = "Signal is not configured on this instance." });

        var ok = await _dir.SetSignalNumberAsync(owner, number, ct);
        if (!ok) return Conflict(new { error = "This number is already linked to another account." });

        var code = await _codes.CreateAsync(owner, "signal-verify", number, ct);
        // Send failure → still 202; the user retries by saving the number again.
        await _signal.SendAsync(number, $"Open AgentHub verification code: {code}", ct);
        return Accepted();
    }

    /// <summary>
    /// Verify attempts per owner within a sliding 10-minute window. In-memory on purpose: codes are
    /// 6 digits with a 10-minute TTL, so brute force must be blocked at the consumer. Static — the
    /// controller is per-request.
    /// </summary>
    private static readonly ConcurrentDictionary<string, (int Failures, DateTimeOffset WindowStart)> _verifyFailures = new();
    private const int MaxVerifyFailures = 5;
    private static readonly TimeSpan VerifyWindow = TimeSpan.FromMinutes(10);

    /// <summary>Confirms the code sent to the user's Signal number and marks the number verified.</summary>
    [HttpPost("signal/verify")]
    public async Task<IActionResult> VerifySignal([FromBody] SignalVerify body, CancellationToken ct)
    {
        var owner = Owner;
        var now = DateTimeOffset.UtcNow;
        var entry = _verifyFailures.TryGetValue(owner, out var e) && now - e.WindowStart < VerifyWindow
            ? e : (Failures: 0, WindowStart: now);
        if (entry.Failures >= MaxVerifyFailures)
            return StatusCode(StatusCodes.Status429TooManyRequests,
                new { error = "Too many attempts. Try again later." });

        IActionResult Fail()
        {
            _verifyFailures[owner] = (entry.Failures + 1, entry.WindowStart);
            return BadRequest(new { error = "Invalid or expired code." });
        }

        var consumed = await _codes.ConsumeAsync(body.Code?.Trim() ?? "", "signal-verify", ct);
        if (consumed is null) return Fail();

        // The code must belong to the caller AND match the number currently on file —
        // a code minted for a previously saved number is worthless.
        var user = await _dir.GetAsync(owner, ct);
        var (codeOwner, payload) = consumed.Value;
        if (codeOwner != owner || payload is null || payload != user?.SignalNumber) return Fail();

        await _dir.SetSignalVerifiedAsync(owner, true, ct);
        _verifyFailures.TryRemove(owner, out _);
        return NoContent();
    }
}
