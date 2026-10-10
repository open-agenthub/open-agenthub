using System.Text;
using System.Text.Json;
using AgentHub.Api.Models;
using AgentHub.Api.Services;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>
/// Identity extraction is display only and unverified (docs/provider-accounts.md); these pin
/// which fields each provider's file is read from and that every malformed input yields null.
/// </summary>
public sealed class ProviderAccountIdentityReaderTests
{
    private static string Base64Url(string json) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Jwt(string payloadJson) => $"eyJhbGciOiJub25lIn0.{Base64Url(payloadJson)}.signature-not-checked";

    [Fact]
    public void Codex_ReadsEmailAndAccountIdFromTheIdTokenPayload()
    {
        var token = Jwt("{\"email\":\"dev@example.com\",\"https://api.openai.com/auth\":{\"chatgpt_account_id\":\"acct_42\"}}");
        var json = $"{{\"tokens\":{{\"id_token\":\"{token}\",\"access_token\":\"x\"}}}}";

        var identity = ProviderAccountIdentityReader.FromFile(AgentKind.Codex, json);

        Assert.Equal(new ProviderAccountIdentity("acct_42", "dev@example.com", null), identity);
    }

    [Fact]
    public void Codex_FallsBackToTheEmailAsKey()
    {
        var json = $"{{\"tokens\":{{\"id_token\":\"{Jwt("{\"email\":\"dev@example.com\"}")}\"}}}}";

        var identity = ProviderAccountIdentityReader.FromFile(AgentKind.Codex, json);

        Assert.Equal("dev@example.com", identity!.Key);
    }

    [Fact]
    public void Cursor_ReadsSubjectAndEmailFromTheAccessToken()
    {
        var json = $"{{\"accessToken\":\"{Jwt("{\"sub\":\"auth0|user_1\",\"email\":\"c@example.com\"}")}\",\"refreshToken\":\"r\"}}";

        var identity = ProviderAccountIdentityReader.FromFile(AgentKind.Cursor, json);

        Assert.Equal(new ProviderAccountIdentity("auth0|user_1", "c@example.com", null), identity);
    }

    [Fact]
    public void OpenClaw_UsesTheProfileNamesAndTheFirstEmail()
    {
        const string json = """
            {"profiles":{"openai:work":{"type":"oauth"},"anthropic:default":{"type":"oauth","email":"o@example.com"}}}
            """;

        var identity = ProviderAccountIdentityReader.FromFile(AgentKind.OpenClaw, json);

        Assert.Equal("anthropic:default,openai:work", identity!.Key);
        Assert.Equal("o@example.com", identity.Email);
        Assert.Null(identity.Organization);
    }

    [Fact]
    public void OpenClaw_SingleProfileNameDoublesAsOrganization()
    {
        var identity = ProviderAccountIdentityReader.FromFile(AgentKind.OpenClaw,
            "{\"profiles\":{\"anthropic:default\":{\"type\":\"api_key\"}}}");

        Assert.Equal("anthropic:default", identity!.Organization);
    }

    // An OpenCode Go login is an API key and names nobody. Two keys for the same provider must
    // not share a matching key, or the second login would be filed under the first account.
    [Fact]
    public void OpenCode_KeysTwoApiKeysForTheSameProviderApart()
    {
        var first = ProviderAccountIdentityReader.FromFile(AgentKind.OpenCode,
            "{\"opencode-go\":{\"type\":\"api\",\"key\":\"sk-first\"}}");
        var second = ProviderAccountIdentityReader.FromFile(AgentKind.OpenCode,
            "{\"opencode-go\":{\"type\":\"api\",\"key\":\"sk-second\"}}");

        Assert.Equal("opencode-go", first!.Organization);
        Assert.Null(first.Email);
        Assert.NotEqual(first.Key, second!.Key);
        Assert.DoesNotContain("sk-first", first.Key);
    }

    // An oauth entry rotates its tokens, so only the account id (when present) tells two apart;
    // a rotation must keep the same key or every refresh would become a new account.
    [Fact]
    public void OpenCode_OauthKeyIgnoresRotatingTokensAndListsEveryProvider()
    {
        var before = ProviderAccountIdentityReader.FromFile(AgentKind.OpenCode,
            "{\"openai\":{\"type\":\"oauth\",\"access\":\"a1\",\"refresh\":\"r1\",\"expires\":1,\"accountId\":\"acct\"},"
            + "\"opencode-go\":{\"type\":\"api\",\"key\":\"k\"}}");
        var after = ProviderAccountIdentityReader.FromFile(AgentKind.OpenCode,
            "{\"opencode-go\":{\"type\":\"api\",\"key\":\"k\"},"
            + "\"openai\":{\"type\":\"oauth\",\"access\":\"a2\",\"refresh\":\"r2\",\"expires\":2,\"accountId\":\"acct\"}}");

        Assert.Equal(before!.Key, after!.Key);
        Assert.StartsWith("openai:acct,opencode-go:", before.Key);
        Assert.Equal("openai, opencode-go", before.Organization);
    }

    [Fact]
    public void Claude_HasNoIdentityInItsFile()
        => Assert.Null(ProviderAccountIdentityReader.FromFile(AgentKind.Claude, "{\"claudeAiOauth\":{\"accessToken\":\"x\"}}"));

    [Theory]
    [InlineData(AgentKind.Codex, "not json")]
    [InlineData(AgentKind.Codex, "{\"tokens\":{\"id_token\":\"garbage\"}}")]
    [InlineData(AgentKind.Codex, "{\"tokens\":{\"id_token\":\"a.!!!.c\"}}")]
    [InlineData(AgentKind.Codex, "{\"tokens\":{}}")]
    [InlineData(AgentKind.Cursor, "{\"accessToken\":\"\"}")]
    [InlineData(AgentKind.Cursor, "[]")]
    [InlineData(AgentKind.OpenClaw, "{\"profiles\":{}}")]
    [InlineData(AgentKind.OpenCode, "{}")]
    [InlineData(AgentKind.OpenCode, "{\"opencode-go\":\"key\"}")]
    public void MalformedInputYieldsNull(AgentKind agent, string json)
        => Assert.Null(ProviderAccountIdentityReader.FromFile(agent, json));

    [Fact]
    public void Header_DecodesBase64UrlJsonAndCleansValues()
    {
        var header = Base64Url("{\"key\":\"uuid-1:org-1\",\"email\":\" me@example.com \",\"organization\":\"Örg \\u0007GmbH\"}");

        var identity = ProviderAccountIdentityReader.FromHeader(header);

        Assert.Equal(new ProviderAccountIdentity("uuid-1:org-1", "me@example.com", "Örg GmbH"), identity);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("!!!not base64!!!")]
    [InlineData("bm90IGpzb24")] // "not json"
    [InlineData("W10")] // "[]"
    [InlineData("e30")] // "{}"
    public void Header_MalformedOrEmptyYieldsNull(string? header)
        => Assert.Null(ProviderAccountIdentityReader.FromHeader(header));

    [Fact]
    public void Header_OversizedIsIgnored()
    {
        var header = Base64Url(JsonSerializer.Serialize(new { key = new string('k', 5000) }));
        Assert.Null(ProviderAccountIdentityReader.FromHeader(header));
    }

    [Fact]
    public void Header_FieldsAreBounded()
    {
        var identity = ProviderAccountIdentityReader.FromHeader(Base64Url(JsonSerializer.Serialize(new { email = new string('e', 500) })));
        Assert.Equal(200, identity!.Email!.Length);
    }

    [Fact]
    public void Display_CombinesEmailAndOrganization()
    {
        Assert.Null(new ProviderAccountIdentity("k", null, null).Display);
        Assert.Equal("a@example.com", new ProviderAccountIdentity("k", "a@example.com", null).Display);
        Assert.Equal("Org", new ProviderAccountIdentity("k", null, "Org").Display);
        Assert.Equal("a@example.com · Org", new ProviderAccountIdentity("k", "a@example.com", "Org").Display);
    }
}
