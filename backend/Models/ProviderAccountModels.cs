namespace AgentHub.Api.Models;

/// <summary>
/// Who a stored provider login belongs to, as far as the CLI's own files say. Display only:
/// nothing is verified against the provider and nothing is authorised on it — see
/// docs/provider-accounts.md. <see cref="Key"/> is the stable value two uploads are matched on
/// (an account uuid, a JWT subject); <see cref="Email"/> and <see cref="Organization"/> are what
/// a person sees in a dropdown.
/// </summary>
public sealed record ProviderAccountIdentity(string? Key, string? Email, string? Organization)
{
    public bool IsEmpty => Key is null && Email is null && Organization is null;

    /// <summary>The short text shown next to the label, or null when nothing is known.</summary>
    public string? Display => (Email, Organization) switch
    {
        (null, null) => null,
        (var email, null) => email,
        (null, var org) => org,
        var (email, org) => $"{email} · {org}"
    };
}

/// <summary>One entry of the <c>accounts.json</c> index inside a provider secret.</summary>
public sealed class ProviderAccount
{
    public required string Id { get; init; }
    public string Label { get; set; } = "";
    public ProviderAccountIdentity? Identity { get; set; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime? LastUsedAt { get; set; }
    public bool IsDefault { get; set; }
    /// <summary>
    /// Until when the account is at its usage limit (docs/account-limits.md); null or past =
    /// usable. A timestamp rather than a flag so nothing has to clear it: the mark expires on
    /// its own once the provider's window has reset.
    /// </summary>
    public DateTime? ExhaustedUntil { get; set; }
    /// <summary>What reported the limit (a rate-limit window, the CLI line that matched).</summary>
    public string? ExhaustedReason { get; set; }
}

/// <summary>
/// What the API returns about an account — never the file, never the matching key.
/// <see cref="IsExhausted"/> is computed at listing time, so a client needs no clock of its own
/// to know whether <see cref="ExhaustedUntil"/> still applies.
/// </summary>
public sealed record ProviderAccountInfo(
    string Id, string Label, string? Email, string? Organization,
    DateTime CreatedAt, DateTime? LastUsedAt, bool IsDefault,
    DateTime? ExhaustedUntil = null, string? ExhaustedReason = null, bool IsExhausted = false)
{
    public static ProviderAccountInfo From(ProviderAccount account, DateTime? now = null)
    {
        var exhausted = Services.ProviderAccountSecret.IsExhausted(account, now ?? DateTime.UtcNow);
        return new(
            account.Id, account.Label, account.Identity?.Email, account.Identity?.Organization,
            account.CreatedAt, account.LastUsedAt, account.IsDefault,
            exhausted ? account.ExhaustedUntil : null, exhausted ? account.ExhaustedReason : null, exhausted);
    }
}

/// <summary>Partial update of an account: null = unchanged. <paramref name="ClearExhausted"/>
/// lifts a usage-limit mark by hand (docs/account-limits.md, "Where the detection is weak").</summary>
public sealed record UpdateProviderAccountRequest(string? Label = null, bool? IsDefault = null, bool? ClearExhausted = null);

/// <summary>
/// Body of <c>POST /internal/sessions/{id}/account-exhausted</c>: the pod says its account hit a
/// usage limit. <paramref name="Source"/> is <c>mod</c> (Claude's in-process mod read the
/// rate-limit windows) or <c>output</c> (the session agent matched the CLI's own notice);
/// <paramref name="ResetsAt"/> is kept when the report has one, else the default applies.
/// </summary>
public sealed record AccountExhaustedReport(string? Source, string? Kind = null, double? PercentUsed = null,
    DateTime? ResetsAt = null, string? Detail = null);

/// <summary>Body of <c>PATCH /api/sessions/{id}/credential</c>.</summary>
public sealed record SwitchSessionCredentialRequest(string CredentialId);
