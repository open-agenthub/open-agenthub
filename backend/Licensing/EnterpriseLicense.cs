using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentHub.Api.Licensing;

/// <summary>Current enterprise license status (derived from the activated token).</summary>
public sealed record LicenseStatus
{
    public bool Valid { get; init; }
    public bool Present { get; init; }        // a token is stored (valid or not)
    public string? Plan { get; init; }        // trial | subscription | granted
    public int Seats { get; init; }
    public string? Org { get; init; }
    public string? Email { get; init; }
    public DateTime? ValidUntil { get; init; }
    public string Reason { get; init; } = "";  // why invalid (for the admin UI)
}

/// <summary>
/// Verifies the offline license token (compact ES256 JWS) against a configured public
/// key and gates enterprise features. The token itself is activated through the admin
/// UI and stored in the database — it is intentionally NOT a chart value, so enterprise
/// features cannot be unlocked by flipping a Helm flag. Verification is fully offline.
/// </summary>
public interface IEnterpriseLicense
{
    LicenseStatus Status { get; }
    /// <summary>True if enterprise features may run (valid, unexpired license).</summary>
    bool Enabled { get; }
    /// <summary>Re-reads the token from the store and re-verifies it (called at startup and after activation).</summary>
    Task ReloadAsync(CancellationToken ct = default);
}

public sealed class EnterpriseLicense : IEnterpriseLicense
{
    private readonly ILicenseStore _store;
    private readonly string? _publicKeyPem;
    private readonly ILogger<EnterpriseLicense> _log;

    // Set by ReloadAsync; read (lock-free) by Status. volatile makes the swap visible across threads.
    private volatile LicenseClaims? _claims;
    private volatile bool _hasToken;

    public EnterpriseLicense(IConfiguration cfg, ILicenseStore store, ILogger<EnterpriseLicense> log)
    {
        _store = store;
        _publicKeyPem = cfg["License:PublicKey"];
        _log = log;
    }

    public async Task ReloadAsync(CancellationToken ct = default)
    {
        var token = await _store.GetTokenAsync(ct);
        _hasToken = !string.IsNullOrWhiteSpace(token);
        _claims = Verify(token);
    }

    public LicenseStatus Status
    {
        get
        {
            var claims = _claims;
            if (claims is null)
                return new LicenseStatus
                {
                    Valid = false, Present = _hasToken,
                    Reason = !_hasToken ? "No license activated." : "License token is invalid."
                };
            var expired = claims.ValidUntil <= DateTime.UtcNow;
            return new LicenseStatus
            {
                Valid = !expired, Present = true,
                Plan = claims.Plan, Seats = claims.Seats, Org = claims.Org, Email = claims.Email,
                ValidUntil = claims.ValidUntil,
                Reason = expired ? "License expired." : ""
            };
        }
    }

    public bool Enabled => Status.Valid;

    private LicenseClaims? Verify(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(_publicKeyPem))
            return null;
        try
        {
            var parts = token.Split('.');
            if (parts.Length != 3) return null;
            var signingInput = $"{parts[0]}.{parts[1]}";
            var signature = Base64UrlDecode(parts[2]);

            using var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(_publicKeyPem);
            var ok = ecdsa.VerifyData(
                Encoding.ASCII.GetBytes(signingInput), signature,
                HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            if (!ok) { _log.LogWarning("License token signature verification failed."); return null; }

            using var payload = JsonDocument.Parse(Base64UrlDecode(parts[1]));
            var root = payload.RootElement;
            return new LicenseClaims(
                root.TryGetProperty("org", out var o) ? o.GetString() : null,
                root.TryGetProperty("email", out var e) ? e.GetString() : null,
                root.TryGetProperty("seats", out var s) ? s.GetInt32() : 0,
                root.TryGetProperty("plan", out var p) ? p.GetString() : null,
                root.TryGetProperty("validUntil", out var v) && v.GetString() is { } vs
                    ? DateTime.Parse(vs, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal)
                    : DateTime.MinValue);
        }
        catch (Exception ex) { _log.LogWarning(ex, "License token could not be parsed."); return null; }
    }

    private static byte[] Base64UrlDecode(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        s = (s.Length % 4) switch { 2 => s + "==", 3 => s + "=", _ => s };
        return Convert.FromBase64String(s);
    }

    private sealed record LicenseClaims(string? Org, string? Email, int Seats, string? Plan, DateTime ValidUntil);
}
