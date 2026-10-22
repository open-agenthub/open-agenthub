using NCrontab;
using AgentHub.Api.Models;

namespace AgentHub.Api.Services;

/// <summary>UTC cron validation shared by create and update, and the next planned occurrence.</summary>
public static class SessionSchedule
{
    public static string? Normalize(SessionMode mode, string? value)
    {
        if (mode != SessionMode.Scheduled)
        {
            if (value is not null) throw new ArgumentException("Schedule is only available for Scheduled sessions.");
            return null;
        }
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128)
            throw new ArgumentException("Schedule must be a UTC cron expression (at most 128 characters).");
        var normalized = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        try { Parse(normalized); }
        catch (Exception error) when (error is CrontabException or ArgumentException)
        { throw new ArgumentException("Schedule must be a valid five-field UTC cron expression.", error); }
        return normalized;
    }

    public static DateTimeOffset? NextRun(string? value, DateTimeOffset after)
    {
        if (value is null) return null;
        try
        {
            var next = Parse(value).Select(s => s.GetNextOccurrence(after.UtcDateTime)).Min();
            return next == DateTime.MaxValue ? null : new DateTimeOffset(DateTime.SpecifyKind(next, DateTimeKind.Utc));
        }
        // Old rows were accepted by the API server alone. An unreadable legacy expression must
        // not make the entire session listing fail.
        catch (Exception error) when (error is CrontabException or ArgumentException) { return null; }
    }

    private static CrontabSchedule[] Parse(string expression)
    {
        expression = expression switch
        {
            "@yearly" or "@annually" => "0 0 1 1 *",
            "@monthly" => "0 0 1 * *",
            "@weekly" => "0 0 * * 0",
            "@daily" or "@midnight" => "0 0 * * *",
            "@hourly" => "0 * * * *",
            _ => expression
        };
        var fields = expression.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5) throw new ArgumentException("Five fields required.");
        for (var i = 0; i < fields.Length; i++) fields[i] = fields[i].Replace('?', '*');
        // The parser also accepts abbreviated or full names that the controller rejects.
        // Keep the public syntax to the controller's three-letter month and weekday names.
        if (System.Text.RegularExpressions.Regex.Matches(string.Join(' ', fields), "[A-Za-z]+")
            .Any(match => match.Value.Length != 3))
            throw new ArgumentException("Use three-letter month and weekday names.");
        var parsed = CrontabSchedule.Parse(string.Join(' ', fields));
        // Kubernetes matches either day field when both are restricted; the parser intersects
        // them. Union two schedules so the displayed next run follows the controller's rule.
        if (!HasWildcard(fields[2]) && !HasWildcard(fields[4]))
        {
            var monthDay = (string[])fields.Clone(); monthDay[4] = "*";
            var weekDay = (string[])fields.Clone(); weekDay[2] = "*";
            return [CrontabSchedule.Parse(string.Join(' ', monthDay)), CrontabSchedule.Parse(string.Join(' ', weekDay))];
        }
        return [parsed];
    }

    private static bool HasWildcard(string field) => field.Split(',').Any(part =>
    {
        var pieces = part.Split('/');
        return pieces[0] == "*" && (pieces.Length == 1 || int.TryParse(pieces[1], out var step) && step == 1);
    });
}
