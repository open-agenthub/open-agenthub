using AgentHub.Api.Models;
using AgentHub.Api.Notifications;
using AgentHub.Api.Persistence;

namespace AgentHub.Api.Services;

/// <summary>What the hub did with a pod's usage-limit report (docs/account-limits.md).</summary>
public sealed record AccountFailoverOutcome(
    /// <summary>The account that was marked, or null when the session runs on none (API key).</summary>
    string? AccountId,
    DateTime? ExhaustedUntil,
    /// <summary><c>ignored</c> (no account), <c>marked</c> (no switch wanted or possible),
    /// <c>switched</c>, or <c>no_alternative</c>.</summary>
    string Action,
    string? SwitchedTo = null)
{
    public const string Ignored = "ignored";
    public const string Marked = "marked";
    public const string Switched = "switched";
    public const string NoAlternative = "no_alternative";
}

/// <summary>Takes a pod's report that its account is at a usage limit and acts on it.</summary>
public interface IAccountFailover
{
    Task<AccountFailoverOutcome> HandleAsync(SessionRecord session, AccountExhaustedReport report, CancellationToken ct);
}

/// <summary>
/// Marks the session's account exhausted and tells the notifiers. The report names no account:
/// the pod does not know which login it mounted, so the hub reads it off the record — the pin
/// when there is one, else what the last spawn resolved — and refuses to guess beyond that.
/// </summary>
public sealed class AccountFailover : IAccountFailover
{
    public const string ExhaustedEvent = "account-exhausted";

    /// <summary>How long a report without a reset time keeps the account out of the rotation.</summary>
    public static readonly TimeSpan DefaultExhaustedFor = TimeSpan.FromHours(1);
    /// <summary>A reset time further out than this is treated as a mistake and clamped: a
    /// mis-parsed year would otherwise retire an account for good.</summary>
    public static readonly TimeSpan MaxExhaustedFor = TimeSpan.FromDays(8);

    private readonly ISessionService _sessions;
    private readonly IEnumerable<INotifier> _notifiers;
    private readonly ILogger<AccountFailover> _log;
    private readonly TimeSpan _defaultExhaustedFor;
    private readonly Func<DateTime> _now;

    public AccountFailover(ISessionService sessions, IEnumerable<INotifier> notifiers, IConfiguration configuration,
        ILogger<AccountFailover> log, Func<DateTime>? now = null)
    {
        _sessions = sessions;
        _notifiers = notifiers;
        _log = log;
        _now = now ?? (() => DateTime.UtcNow);
        var seconds = configuration.GetValue("AgentHub:AccountExhaustedDefaultSeconds", (int)DefaultExhaustedFor.TotalSeconds);
        _defaultExhaustedFor = seconds > 0 ? TimeSpan.FromSeconds(seconds) : DefaultExhaustedFor;
    }

    public async Task<AccountFailoverOutcome> HandleAsync(SessionRecord session, AccountExhaustedReport report, CancellationToken ct)
    {
        var accountId = session.CredentialId ?? session.ResolvedCredentialId;
        // An API-key session has no account to retire, and a session that never mounted one
        // (no login stored) has nothing the next start could avoid.
        if (accountId is null || session.AuthMode == AgentAuthMode.ApiKey)
            return new AccountFailoverOutcome(null, null, AccountFailoverOutcome.Ignored);

        var now = _now();
        var until = ExhaustedUntil(report, now);
        var marked = await _sessions.MarkProviderAccountExhaustedAsync(session.Owner, session.Agent, accountId, until, Reason(report), ct);
        if (marked is null)
        {
            _log.LogWarning("Session {Id} reported a usage limit for {Agent} account {Account}, which no longer exists",
                session.Id, session.Agent, accountId);
            return new AccountFailoverOutcome(null, null, AccountFailoverOutcome.Ignored);
        }
        _log.LogInformation("Session {Id}: {Agent} account {Account} is at its usage limit until {Until} ({Source})",
            session.Id, session.Agent, accountId, until, report.Source ?? "unknown");
        await NotifyAsync(session, ExhaustedEvent,
            $"{session.Agent} account \"{marked.Label}\" hit its usage limit (resets {until:HH:mm 'UTC'}).", ct);
        return new AccountFailoverOutcome(accountId, until, AccountFailoverOutcome.Marked);
    }

    /// <summary>The reset time the report carries, bounded to the near future; else the default.</summary>
    public DateTime ExhaustedUntil(AccountExhaustedReport report, DateTime now)
    {
        var fallback = now + _defaultExhaustedFor;
        if (report.ResetsAt is not { } resetsAt) return fallback;
        var utc = resetsAt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(resetsAt, DateTimeKind.Utc) : resetsAt.ToUniversalTime();
        if (utc <= now) return fallback;
        return utc > now + MaxExhaustedFor ? now + MaxExhaustedFor : utc;
    }

    private static string Reason(AccountExhaustedReport report)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(report.Kind)) parts.Add(report.Kind.Trim());
        if (report.PercentUsed is { } percent) parts.Add($"{Math.Round(percent)}%");
        if (parts.Count == 0 && !string.IsNullOrWhiteSpace(report.Detail)) parts.Add(Truncate(report.Detail.Trim(), 200));
        parts.Insert(0, report.Source == "mod" ? "mod" : "output");
        return string.Join(" ", parts);
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    private async Task NotifyAsync(SessionRecord session, string ev, string message, CancellationToken ct)
    {
        // Every notifier swallows its own failures, as InternalController relies on too.
        await Task.WhenAll(_notifiers.Select(n => n.NotifyAsync(session, ev, message, ct)));
    }
}
