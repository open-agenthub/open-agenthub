using System.Text;
using AgentHub.Api.Models;
using AgentHub.Api.Services;
using Xunit;

namespace AgentHub.Api.Tests;

public class CredentialSecretFactoryTests
{
    [Fact]
    public void GeneralCredentials_MergeClearAndReportOpenAiKey()
    {
        var secret = CredentialSecretFactory.CreateGeneralSecret("creds-owner", "sessions", "owner", new Dictionary<string, byte[]>
        {
            ["anthropic_api_key"] = Encoding.UTF8.GetBytes("old"),
            ["unrelated"] = Encoding.UTF8.GetBytes("keep")
        }, new UserCredentials
        {
            OpenAiApiKey = "new",
            Clear = ["anthropicApiKey"]
        });

        Assert.True(secret.Data.ContainsKey("openai_api_key"));
        Assert.False(secret.Data.ContainsKey("anthropic_api_key"));
        Assert.True(secret.Data.ContainsKey("unrelated"));
        Assert.True(CredentialSecretFactory.CredentialStatus(secret.Data).OpenAiApiKey);
    }

    [Theory]
    [InlineData(AgentKind.Claude, "credentials.json", "auth.json")]
    [InlineData(AgentKind.Codex, "auth.json", "credentials.json")]
    // Pinned from Cursor Agent CLI file store: auth.json (distinct Secret from Codex).
    [InlineData(AgentKind.Cursor, "auth.json", "credentials.json")]
    // Pinned from OpenClaw 2026.7.1-2: auth-profiles.json (logical JSON / SQLite store_json).
    [InlineData(AgentKind.OpenClaw, "auth-profiles.json", "auth.json")]
    public void ProviderCredentials_WriteOnlyTheMatchingProviderFile(AgentKind agent, string expectedKey, string otherKey)
    {
        var json = agent switch
        {
            AgentKind.Claude => "{\"claudeAiOauth\":{}}",
            AgentKind.Codex => "{\"tokens\":{}}",
            AgentKind.OpenClaw => "{\"version\":1,\"profiles\":{\"anthropic:default\":{\"type\":\"api_key\",\"provider\":\"anthropic\",\"key\":\"synthetic-key-not-real\"}}}",
            _ => "{\"accessToken\":\"synthetic-test-token-not-real\",\"refreshToken\":\"synthetic-refresh\"}"
        };

        var secret = CredentialSecretFactory.CreateProviderSecret($"{agent}-owner", "sessions", "owner", agent, json);

        // The account layout from the start (docs/provider-accounts.md): an index plus one
        // <accountId>.<file> key, never the bare legacy file and never another provider's name.
        Assert.Equal($"{agent}-owner", secret.Metadata.Name);
        Assert.True(secret.Data.ContainsKey(ProviderAccountSecret.IndexKey));
        var fileKey = Assert.Single(secret.Data.Keys.Where(k => k != ProviderAccountSecret.IndexKey));
        Assert.EndsWith("." + expectedKey, fileKey);
        Assert.False(secret.Data.ContainsKey(expectedKey));
        Assert.DoesNotContain(secret.Data.Keys, k => k.EndsWith("." + otherKey, StringComparison.Ordinal) && !k.EndsWith("." + expectedKey, StringComparison.Ordinal));
        Assert.Equal(2, secret.Data.Count);
    }

    [Fact]
    public void CredentialStatus_ReportsProviderSecretsAsSeparateBooleans()
    {
        var status = CredentialSecretFactory.CredentialStatus(
            new Dictionary<string, byte[]>(),
            new Dictionary<string, byte[]> { ["credentials.json"] = Encoding.UTF8.GetBytes("secret") },
            new Dictionary<string, byte[]>(),
            new Dictionary<string, byte[]> { ["auth.json"] = Encoding.UTF8.GetBytes("secret") },
            new Dictionary<string, byte[]> { ["auth-profiles.json"] = Encoding.UTF8.GetBytes("secret") });

        Assert.True(status.ClaudeSubscription);
        Assert.False(status.CodexSubscription);
        Assert.True(status.CursorSubscription);
        Assert.True(status.OpenclawSubscription);
    }

    [Fact]
    public void GeneralCredentials_MergeClearAndReportCursorApiKey()
    {
        var secret = CredentialSecretFactory.CreateGeneralSecret("creds-owner", "sessions", "owner", null, new UserCredentials
        {
            CursorApiKey = "synthetic-cursor-api-key-not-real"
        });

        Assert.True(secret.Data.ContainsKey("cursor_api_key"));
        Assert.True(CredentialSecretFactory.CredentialStatus(secret.Data).CursorApiKey);
        Assert.Equal("cursor_api_key", CredentialSecretFactory.CredentialKey(nameof(UserCredentials.CursorApiKey)));
    }
}
