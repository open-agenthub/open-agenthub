using AgentHub.Api.Models;
using AgentHub.Api.Services;
using Xunit;

namespace AgentHub.Api.Tests;

public class ProviderCredentialValidatorTests
{
    [Theory]
    [InlineData(AgentKind.Claude, "{\"claudeAiOauth\":{\"accessToken\":\"x\"}}", true)]
    [InlineData(AgentKind.Codex, "{\"tokens\":{\"access_token\":\"x\"}}", true)]
    [InlineData(AgentKind.Claude, "{\"tokens\":{}}", false)]
    [InlineData(AgentKind.Codex, "{\"claudeAiOauth\":{}}", false)]
    [InlineData(AgentKind.Codex, "{}", false)]
    [InlineData(AgentKind.Codex, "not-json", false)]
    [InlineData(AgentKind.Codex, "[]", false)]
    public void Validate_RequiresProviderShape(AgentKind agent, string json, bool expected)
        => Assert.Equal(expected, ProviderCredentialValidator.Validate(agent, json));

    [Fact]
    public void Validate_RejectsPayloadLargerThan64KiB()
    {
        var json = "{\"tokens\":{\"access_token\":\"" + new string('x', ProviderCredentialValidator.MaxBytes) + "\"}}";

        Assert.False(ProviderCredentialValidator.Validate(AgentKind.Codex, json));
    }

    [Fact]
    public void Validate_InvalidJsonDoesNotExposeCredentialValue()
    {
        const string secret = "credential-value-must-never-be-exposed";
        var exception = Record.Exception(() => ProviderCredentialValidator.Validate(AgentKind.Codex, $"{{\"tokens\":\"{secret}"));

        Assert.Null(exception);
    }
}
