using AgentHub.Api.Models;
using AgentHub.Api.Notifications;
using AgentHub.Api.Persistence;

namespace AgentHub.Api.Services;

/// <summary>What the hub did with a pod's usage-limit report (docs/account-limits.md).</summary>
public sealed record AccountFailoverOutcome(
    /// <summary>The account that was marked, or null when the session runs on none (API key).</summary>
    string? AccountId,
    DateTime? ExhaustedUntil,
    /// <summary><c>ignored</c> (no account), <c>marked</c> (switching is off, or the session is
    /// not running), <c>switched</c>, <c>no_alternative</c>, or <c>switch_failed</c>.</summary>
    string Action,
    string? SwitchedTo = null)
{
    public const string Ignored = "ignored";
    public const string Marked = "marked";
    public const string Switched = "switched";
    public const string NoAlternative = "no_alternative";
    public const string SwitchFailed = "switch_failed";
}

/// <summary>Takes a pod's report that its account is at a usage limit and acts on it.</summary>
public interface IAccountFailover
{
    Task<AccountFailoverOutcome> HandleAsync(SessionRecord session, AccountExhaustedReport report, CancellationToken ct);
}

/// <summary>
/// Marks the session's account exhausted, moves the session to another account where it can,
/// and tells the session and the notifiers. The report names no account: the pod does not know
/// which login it mounted, so the hub reads it off the record — the pin when there is one, else
/// what the last spawn resolved — and refuses to guess beyond that.
/// </summary>
public sealed class AccountFailover : IAccountFailover
{
    public const string ExhaustedEvent = "account-exhausted";
    public const string SwitchedEvent = "account-switched";

    /// <summary>How long a report without a reset time keeps the account out of the rotation.</summary>
    public static readonly TimeSpan DefaultExhaustedFor = TimeSpan.FromHours(1);
    /// <summary>A reset time further out than this is treated as a mistake and clamped: a
    /// mis-parsed year would otherwise retire an account for good.</summary>
    public static readonly TimeSpan MaxExhaustedFor = TimeSpan.FromDays(8);

    private readonly ISessionService _sessions;
    private readonly IEnumerable<INotifier> _notifiers;
    private readonly ILogger<AccountFailover> _log;
    private readonly ISessionMessageStore? _messages;
    private readonly ISessionMessageDelivery? _delivery;
    private readonly TimeSpan _defaultExhaustedFor;
    private readonly Func<DateTime> _now;

    public AccountFailover(ISessionService sessions, IEnumerable<INotifier> notifiers, IConfiguration configuration,
        ILogger<AccountFailover> log, ISessionMessageStore? messages = null, ISessionMessageDelivery? delivery = null,
        Func<DateTime>? now = null)
    {
        _sessions = sessions;
        _notifiers = notifiers;
        _log = log;
        _messages = messages;
        _delivery = delivery;
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
        var limit = $"{session.Agent} account \"{marked.Label}\" hit its usage limit (resets {until:HH:mm} UTC)";

        var alternative = await AlternativeAsync(session, accountId, ct);
        if (alternative is not AlternativeFound found)
        {
            await NotifyAsync(session, ExhaustedEvent, $"{limit}; {((NoAlternative)alternative).Why}.", ct);
            return new AccountFailoverOutcome(accountId, until, ((NoAlternative)alternative).Action);
        }

        var reason = $"Switched to account \"{found.Account.Label}\" because \"{marked.Label}\" hit its usage limit (resets {until:HH:mm} UTC)";
        try
        {
            await _sessions.SwitchSessionCredentialAsync(session.Owner, session.Id, found.Account.Id, reason, ct);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or HttpRequestException)
        {
            // The pod is gone, or finished between the report and the push: the mark stands and
            // the next start avoids the account; nothing to switch any more.
            _log.LogWarning(e, "Session {Id} could not be moved to {Agent} account {Account}", session.Id, session.Agent, found.Account.Id);
            await NotifyAsync(session, ExhaustedEvent, $"{limit}; switching to \"{found.Account.Label}\" failed: {e.Message}", ct);
            return new AccountFailoverOutcome(accountId, until, AccountFailoverOutcome.SwitchFailed);
        }
        _log.LogInformation("Session {Id} moved from {Agent} account {From} to {To} after a usage limit",
            session.Id, session.Agent, accountId, found.Account.Id);

        await TellTheSessionAsync(session, found.Session, reason, ct);
        await NotifyAsync(session, SwitchedEvent, $"{reason}.", ct);
        return new AccountFailoverOutcome(accountId, until, AccountFailoverOutcome.Switched, found.Account.Id);
    }

    private abstract record Alternative;
    private sealed record AlternativeFound(ProviderAccountInfo Account, SessionInfo Session) : Alternative;
    private sealed record NoAlternative(string Action, string Why) : Alternative;

    private async Task<Alternative> AlternativeAsync(SessionRecord session, string accountId, CancellationToken ct)
    {
        if (AccountFailoverMode.IsOff(session.AccountFailover))
            return new NoAlternative(AccountFailoverOutcome.Marked, "automatic switching is off for this session");
        if (session.AuthMode != AgentAuthMode.Subscription)
            return new NoAlternative(AccountFailoverOutcome.Marked, "the session does not run on a stored login");
        var info = await _sessions.GetSessionAsync(session.Owner, session.Id, ct);
        if (info is null || info.Phase != SessionStatus.Running || string.IsNullOrEmpty(info.PodIp))
            return new NoAlternative(AccountFailoverOutcome.Marked, "the session is not running");
        var accounts = await _sessions.ListProviderAccountsAsync(session.Owner, ct);
        var stored = accounts.TryGetValue(session.Agent.ToString(), out var list) ? list : Array.Empty<ProviderAccountInfo>();
        var candidate = PickAlternative(stored, accountId);
        return candidate is null
            ? new NoAlternative(AccountFailoverOutcome.NoAlternative, "no other account is available")
            : new AlternativeFound(candidate, info);
    }

    /// <summary>
    /// The account to move to: another one that is not exhausted, default first, then the one
    /// used longest ago (never-used ahead of used) — the same order as
    /// <see cref="ProviderAccountSecret.NextAvailable"/>, over the listing the service hands out.
    /// </summary>
    public static ProviderAccountInfo? PickAlternative(IReadOnlyList<ProviderAccountInfo> accounts, string currentId) =>
        accounts
            .Where(a => a.Id != currentId && !a.IsExhausted)
            .OrderByDescending(a => a.IsDefault)
            .ThenBy(a => a.LastUsedAt ?? DateTime.MinValue)
            .FirstOrDefault();

    /// <summary>
    /// A priority message from outside the fleet (docs/priority-messages.md), so the agent reads
    /// why its conversation restarted: through the mod for Claude, typed into the terminal for
    /// the others once the TUI is back. Stored first, so a pod that is still restarting leaves
    /// it in the inbox and the session view rather than losing it.
    /// </summary>
    private async Task TellTheSessionAsync(SessionRecord session, SessionInfo target, string text, CancellationToken ct)
    {
        if (_messages is null) return;
        var message = new SessionMessageRecord
        {
            Id = Guid.NewGuid().ToString("n")[..12],
            ProjectId = session.ProjectId,
            FromSessionId = null,
            ToSessionId = session.Id,
            Owner = session.Owner,
            Body = text + ". Continue your work on the new account.",
            Priority = true,
            Interrupt = false
        };
        try
        {
            await _messages.AddAsync(message, ct);
            var delivery = await AgentMessageDispatch.PushAsync(_delivery, target, message, null, ct);
            _log.LogInformation("Session {Id} was told about the account switch ({Via})", session.Id, delivery.Via);
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Session {Id} could not be told about the account switch", session.Id);
        }
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
