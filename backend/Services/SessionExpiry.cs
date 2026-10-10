using System.Text.RegularExpressions;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;

namespace AgentHub.Api.Services;

/// <summary>
/// The rules of a session's self-deletion: what a caller may ask for, how a duration written as
/// text is read, and when a stored session is due. Pure, so the REST validation, the MCP tools and
/// the sweeper cannot drift apart (docs/session-expiry.md).
/// </summary>
public static class SessionExpiry
{
    public const string FromStart = "start";
    public const string FromLastActivity = "lastActivity";

    /// <summary>
    /// Five minutes: below that the 60-second sweep and the once-a-minute touch throttle make the
    /// deadline meaningless. A year as the ceiling is "never" written as a number.
    /// </summary>
    public const int MinSeconds = 300;
    public const int MaxSeconds = 365 * 24 * 3600;

    private static readonly Regex Duration = new(@"^\s*(\d{1,9})\s*([smhd])\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Reads <c>90m</c>, <c>12h</c>, <c>3d</c> or <c>300s</c>. The unit is mandatory: a bare
    /// number from a language model could mean minutes as easily as seconds, and a session
    /// deleted twelve minutes instead of twelve hours later is not a mistake the caller can see
    /// coming. Throws <see cref="ArgumentException"/> for anything else.
    /// </summary>
    public static int ParseDuration(string? text)
    {
        var match = Duration.Match(text ?? "");
        if (!match.Success)
            throw new ArgumentException(
                "autoDeleteAfter must be a number with a unit: 90m, 12h, 3d or 300s.");
        var amount = long.Parse(match.Groups[1].Value);
        var unit = char.ToLowerInvariant(match.Groups[2].Value[0]);
        var seconds = amount * unit switch { 's' => 1, 'm' => 60, 'h' => 3600, _ => 86400 };
        if (seconds > int.MaxValue) throw new ArgumentException("autoDeleteAfter is too large.");
        return ValidateSeconds((int)seconds);
    }

    public static int ValidateSeconds(int seconds)
    {
        if (seconds < MinSeconds || seconds > MaxSeconds)
            throw new ArgumentException(
                $"autoDeleteAfterSeconds must be between {MinSeconds} (5 minutes) and {MaxSeconds} (365 days).");
        return seconds;
    }

    /// <summary>
    /// Normalizes the basis. Omitted means "since last activity", except for a scheduled session,
    /// where a CronJob is never attached to and the only basis that can mean anything is its
    /// start. Asking for <c>lastActivity</c> on one explicitly is refused rather than corrected,
    /// so an API caller learns the rule instead of getting a session that expires on a clock it
    /// did not ask for.
    /// </summary>
    public static string NormalizeFrom(string? from, SessionMode mode)
    {
        var normalized = string.IsNullOrWhiteSpace(from)
            ? (mode == SessionMode.Scheduled ? FromStart : FromLastActivity)
            : from.Trim() switch
            {
                var f when f.Equals(FromStart, StringComparison.OrdinalIgnoreCase) => FromStart,
                var f when f.Equals(FromLastActivity, StringComparison.OrdinalIgnoreCase) => FromLastActivity,
                _ => throw new ArgumentException("autoDeleteFrom must be 'start' or 'lastActivity'.")
            };
        if (mode == SessionMode.Scheduled && normalized == FromLastActivity)
            throw new ArgumentException(
                "A scheduled session is never attached to, so it can only auto-delete after its start.");
        return normalized;
    }

    /// <summary>
    /// The effective pair for a create or duplicate request: null seconds means the feature is
    /// off and the basis is dropped with it, so a record never carries a basis without a deadline.
    /// </summary>
    public static (int? Seconds, string? From) ForCreate(int? seconds, string? from, SessionMode mode)
    {
        if (seconds is null or 0) return (null, null);
        return (ValidateSeconds(seconds.Value), NormalizeFrom(from, mode));
    }

    /// <summary>
    /// Applies a partial update: null leaves a field alone, 0 seconds switches the feature off.
    /// Returns the pair the record should hold afterwards.
    /// </summary>
    public static (int? Seconds, string? From) ForUpdate(SessionRecord record, int? seconds, string? from)
    {
        if (seconds is 0) return (null, null);
        var effectiveSeconds = seconds ?? record.AutoDeleteAfterSeconds;
        if (effectiveSeconds is null)
        {
            // Nothing to count down from: a basis on its own is validated so a typo is still a
            // 400, but only stored once a deadline exists.
            if (from is not null) NormalizeFrom(from, record.Mode);
            return (null, null);
        }
        return (ValidateSeconds(effectiveSeconds.Value),
            NormalizeFrom(from ?? record.AutoDeleteFrom, record.Mode));
    }

    /// <summary>When the session is due, or null when it never is.</summary>
    public static DateTime? ExpiresAt(SessionRecord record)
    {
        if (record.AutoDeleteAfterSeconds is not { } seconds) return null;
        var basis = record.AutoDeleteFrom == FromStart ? record.CreatedAt : record.LastActivityAt;
        return basis.AddSeconds(seconds);
    }

    public static bool IsDue(SessionRecord record, DateTime now) =>
        ExpiresAt(record) is { } expiresAt && expiresAt <= now;
}
