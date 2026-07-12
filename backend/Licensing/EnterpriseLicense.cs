using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentHub.Api.Licensing;

/// <summary>Current enterprise license status (derived from the configured token).</summary>
public sealed record LicenseStatus
{
    public bool Valid { get; init; }
    public string? Plan { get; init; }        // trial | subscription | granted
    public int Seats { get; init; }
    public string? Org { get; init; }
    public string? Email { get; init; }
    public DateTime? ValidUntil { get; init; }
    public string Reason { get; init; } = "";  // why invalid (for the admin UI)
}

/// <summary>
/// Verifies the offline license token issued by the license service (compact
/// ES256 JWS) against a configured public key, and gates enterprise features.
/// No network calls — verification is fully offline.
/// </summary>
public interface IEnterpriseLicense
{
    LicenseStatus Status { get; }
    /// <summary>True if enterprise features may run (valid, unexpired license).</summary>
    bool Enabled { get; }
}

public sealed class EnterpriseLicense : IEnterpriseLicense
{
    private readonly string? _token;
    private readonly string? _publicKeyPem;
    private readonly ILogger<EnterpriseLicense> _log;
    private LicenseClaims? _claims;

    public EnterpriseLicense(IConfiguration cfg, ILogger<EnterpriseLicense> log)
    {
        _log = log;
        _token = cfg["License:Token"];
        _publicKeyPem = cfg["License:PublicKey"];
        _claims = VerifyOnce();
    }

    public LicenseStatus Status
    {
        get
        {
            if (_claims is null)
                return new LicenseStatus { Valid = false, Reason = string.IsNullOrWhiteSpace(_token) ? "No license configured." : "License token is invalid." };
            var expired = _claims.ValidUntil <= DateTime.UtcNow;
            return new LicenseStatus
            {
                Valid = !expired,
                Plan = _claims.Plan, Seats = _claims.Seats, Org = _claims.Org, Email = _claims.Email,
                ValidUntil = _claims.ValidUntil,
                Reason = expired ? "License expired." : ""
            };
        }
    }

    public bool Enabled => Status.Valid;

    private LicenseClaims? VerifyOnce()
    {
        if (string.IsNullOrWhiteSpace(_token) || string.IsNullOrWhiteSpace(_publicKeyPem))
            return null;
        try
        {
            var parts = _token.Split('.');
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
