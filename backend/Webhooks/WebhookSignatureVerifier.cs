using System.Security.Cryptography;
using System.Text;

namespace AgentHub.Api.Webhooks;

/// <summary>
/// Authenticates inbound webhook deliveries. All comparisons are constant-time
/// (<see cref="CryptographicOperations.FixedTimeEquals"/>) so a caller cannot
/// probe the secret byte by byte via response timing.
/// </summary>
public static class WebhookSignatureVerifier
{
    public const string GitLabTokenHeader = "X-Gitlab-Token";
    public const string GitHubSignatureHeader = "X-Hub-Signature-256";

    /// <summary>GitLab sends the shared secret verbatim in <c>X-Gitlab-Token</c>.</summary>
    public static bool VerifyGitLabToken(string? headerValue, string secret)
    {
        if (string.IsNullOrEmpty(headerValue) || string.IsNullOrEmpty(secret)) return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(headerValue), Encoding.UTF8.GetBytes(secret));
    }

    /// <summary>GitHub sends <c>X-Hub-Signature-256: sha256=&lt;hex&gt;</c>, an
    /// HMAC-SHA256 of the raw request body keyed with the shared secret.</summary>
    public static bool VerifyGitHubSignature(string? headerValue, ReadOnlySpan<byte> body, string secret)
    {
        if (string.IsNullOrEmpty(headerValue) || string.IsNullOrEmpty(secret)) return false;
        const string prefix = "sha256=";
        if (!headerValue.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;

        byte[] declared;
        try { declared = Convert.FromHexString(headerValue[prefix.Length..]); }
        catch (FormatException) { return false; }

        var computed = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body);
        return CryptographicOperations.FixedTimeEquals(declared, computed);
    }
}
