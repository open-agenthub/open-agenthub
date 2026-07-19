using System.Security.Claims;
using AgentHub.Api.Chat;
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
public sealed class ChatController : ControllerBase
{
    private readonly TelegramOptions _telegramOpts;
    private readonly TelegramClient _telegram;
    private readonly ChatLinkCodeStore _codes;
    private readonly UserDirectory _dir;

    public ChatController(TelegramOptions telegramOpts, TelegramClient telegram,
        ChatLinkCodeStore codes, UserDirectory dir)
    { _telegramOpts = telegramOpts; _telegram = telegram; _codes = codes; _dir = dir; }

    private string Owner =>
        User.FindFirstValue("preferred_username") ?? User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "dev";

    public sealed record TelegramMe(bool Configured, bool Linked, bool Forum, bool Enabled);
    public sealed record SignalMe(bool Configured, string? Number, bool Verified, bool Enabled);
    public sealed record ChatMe(TelegramMe Telegram, SignalMe Signal);
    public sealed record TelegramPrefs(bool Enabled);

    [HttpGet("me")]
    public async Task<ChatMe> Me(CancellationToken ct)
    {
        var user = await _dir.GetAsync(Owner, ct);
        var telegram = new TelegramMe(
            _telegramOpts.Enabled,
            user?.TelegramChatId is not null,
            user?.TelegramForum ?? false,
            user?.TelegramEnabled ?? true);
        // Signal ships later: the per-user fields already round-trip through the
        // directory; only the Configured flag waits for SignalOptions.
        const bool signalConfigured = false;
        var signal = new SignalMe(
            signalConfigured,
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
}
