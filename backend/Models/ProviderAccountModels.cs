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
}

/// <summary>What the API returns about an account — never the file, never the matching key.</summary>
public sealed record ProviderAccountInfo(
    string Id, string Label, string? Email, string? Organization,
    DateTime CreatedAt, DateTime? LastUsedAt, bool IsDefault)
{
    public static ProviderAccountInfo From(ProviderAccount account) => new(
        account.Id, account.Label, account.Identity?.Email, account.Identity?.Organization,
        account.CreatedAt, account.LastUsedAt, account.IsDefault);
}

/// <summary>Partial update of an account: null = unchanged.</summary>
public sealed record UpdateProviderAccountRequest(string? Label = null, bool? IsDefault = null);

/// <summary>Body of <c>PATCH /api/sessions/{id}/credential</c>.</summary>
public sealed record SwitchSessionCredentialRequest(string CredentialId);
