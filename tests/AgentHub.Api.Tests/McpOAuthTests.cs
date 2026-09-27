using AgentHub.Api.Mcp;
using Xunit;

namespace AgentHub.Api.Tests;

public class McpOAuthTests
{
    [Fact]
    public void The_feature_stays_off_until_a_public_url_is_configured()
    {
        // An unconfigured instance must not map /mcp at all: an open endpoint would let a client
        // connect without ever being challenged, so it looks connected while every call fails.
        Assert.False(new McpOAuthOptions().IsConfigured);
        Assert.False(new McpOAuthOptions { PublicBaseUrl = "   " }.IsConfigured);
        Assert.True(new McpOAuthOptions { PublicBaseUrl = "https://agenthub.example.com" }.IsConfigured);
    }

    [Fact]
    public void The_issuer_and_resource_are_derived_without_a_double_slash()
    {
        // The resource string is compared verbatim against the token audience, so a trailing
        // slash in configuration would produce ".../mcp" on one side and "...//mcp" on the other
        // and every token would be rejected as having the wrong audience.
        var options = new McpOAuthOptions { PublicBaseUrl = "https://agenthub.example.com/" };

        Assert.Equal("https://agenthub.example.com", options.Issuer);
        Assert.Equal("https://agenthub.example.com/mcp", options.McpResource);
    }

    [Theory]
    [InlineData("https://claude.ai/api/mcp/auth_callback", true)]
    [InlineData("http://127.0.0.1:41234/callback", true)]
    [InlineData("http://[::1]:41234/callback", true)]
    // Real clients register this spelling; rejecting it would break them.
    [InlineData("http://localhost:41234/callback", true)]
    // Registration is anonymous, so a plain-http redirect to anywhere else would hand an
    // attacker the authorization code of whoever clicks their link.
    [InlineData("http://attacker.example.com/callback", false)]
    [InlineData("http://10.0.0.5/callback", false)]
    [InlineData("not-a-uri", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Redirect_uris_must_be_https_or_loopback(string? uri, bool acceptable)
        => Assert.Equal(acceptable, McpRedirectUri.IsAcceptable(uri));
}
