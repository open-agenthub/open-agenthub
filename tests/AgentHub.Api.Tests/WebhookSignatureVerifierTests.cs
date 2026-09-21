using System.Security.Cryptography;
using System.Text;
using AgentHub.Api.Webhooks;
using Xunit;

namespace AgentHub.Api.Tests;

public class WebhookSignatureVerifierTests
{
    private const string Secret = "whs_0123456789abcdef";

    private static string Sign(string body, string secret)
        => "sha256=" + Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body))).ToLowerInvariant();

    // ---- GitLab (plain token header) ------------------------------------------

    [Fact]
    public void GitLab_MatchingToken_IsAccepted()
        => Assert.True(WebhookSignatureVerifier.VerifyGitLabToken(Secret, Secret));

    [Theory]
    [InlineData("wrong-token")]
    [InlineData("whs_0123456789abcdeF")] // case-flipped last char
    [InlineData("whs_0123456789abcde")]  // truncated
    [InlineData("")]
    [InlineData(null)]
    public void GitLab_WrongOrMissingToken_IsRejected(string? header)
        => Assert.False(WebhookSignatureVerifier.VerifyGitLabToken(header, Secret));

    [Fact]
    public void GitLab_EmptyConfiguredSecret_NeverAccepts()
        => Assert.False(WebhookSignatureVerifier.VerifyGitLabToken("", ""));

    // ---- GitHub (HMAC-SHA256 over the raw body) --------------------------------

    [Fact]
    public void GitHub_ValidSignature_IsAccepted()
    {
        var body = WebhookTestPayloads.GitHubPullRequestOpened;
        Assert.True(WebhookSignatureVerifier.VerifyGitHubSignature(
            Sign(body, Secret), Encoding.UTF8.GetBytes(body), Secret));
    }

    [Fact]
    public void GitHub_UppercaseHexSignature_IsAccepted()
    {
        const string body = "{}";
        var header = "sha256=" + Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes(body)));
        Assert.True(WebhookSignatureVerifier.VerifyGitHubSignature(
            header, Encoding.UTF8.GetBytes(body), Secret));
    }

    [Fact]
    public void GitHub_SignatureOfDifferentBody_IsRejected()
    {
        var header = Sign("{\"a\":1}", Secret);
        Assert.False(WebhookSignatureVerifier.VerifyGitHubSignature(
            header, Encoding.UTF8.GetBytes("{\"a\":2}"), Secret));
    }

    [Fact]
    public void GitHub_SignatureWithWrongSecret_IsRejected()
    {
        const string body = "{}";
        var header = Sign(body, "other-secret");
        Assert.False(WebhookSignatureVerifier.VerifyGitHubSignature(
            header, Encoding.UTF8.GetBytes(body), Secret));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sha1=abcdef")]           // wrong algorithm prefix
    [InlineData("sha256=not-hex")]        // malformed hex
    [InlineData("sha256=")]               // empty digest
    [InlineData("deadbeef")]              // missing prefix
    public void GitHub_MalformedOrMissingHeader_IsRejected(string? header)
        => Assert.False(WebhookSignatureVerifier.VerifyGitHubSignature(
            header, Encoding.UTF8.GetBytes("{}"), Secret));
}
