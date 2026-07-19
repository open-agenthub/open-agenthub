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
    public void ProviderCredentials_WriteOnlyTheMatchingProviderFile(AgentKind agent, string expectedKey, string otherKey)
    {
        var json = agent == AgentKind.Claude ? "{\"claudeAiOauth\":{}}" : "{\"tokens\":{}}";

        var secret = CredentialSecretFactory.CreateProviderSecret($"{agent}-owner", "sessions", "owner", agent, json);

        Assert.Equal($"{agent}-owner", secret.Metadata.Name);
        Assert.True(secret.Data.ContainsKey(expectedKey));
        Assert.False(secret.Data.ContainsKey(otherKey));
        Assert.Single(secret.Data);
    }
}
