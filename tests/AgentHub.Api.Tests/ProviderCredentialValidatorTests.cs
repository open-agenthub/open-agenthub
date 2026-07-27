using AgentHub.Api.Models;
using AgentHub.Api.Services;
using Xunit;

namespace AgentHub.Api.Tests;

public class ProviderCredentialValidatorTests
{
    [Theory]
    [InlineData(AgentKind.Claude, "{\"claudeAiOauth\":{\"accessToken\":\"x\"}}", true)]
    [InlineData(AgentKind.Codex, "{\"tokens\":{\"access_token\":\"x\"}}", true)]
    // Pinned from Cursor Agent CLI file store (auth.json): non-empty accessToken string.
    [InlineData(AgentKind.Cursor, "{\"accessToken\":\"synthetic-test-token-not-real\",\"refreshToken\":\"synthetic-refresh\"}", true)]
    // Pinned from OpenClaw 2026.7.1-2 auth-profiles store: { version?, profiles, order? }.
    [InlineData(AgentKind.OpenClaw, "{\"version\":1,\"profiles\":{\"anthropic:default\":{\"type\":\"api_key\",\"provider\":\"anthropic\",\"key\":\"x\"}}}", true)]
    [InlineData(AgentKind.Claude, "{\"tokens\":{}}", false)]
    [InlineData(AgentKind.Codex, "{\"claudeAiOauth\":{}}", false)]
    [InlineData(AgentKind.Claude, "{\"accessToken\":\"x\"}", false)]
    [InlineData(AgentKind.Cursor, "{\"tokens\":{}}", false)]
    [InlineData(AgentKind.Cursor, "{\"accessToken\":\"\"}", false)]
    [InlineData(AgentKind.Cursor, "{\"cursorAuth\":{\"accessToken\":\"x\"}}", false)]
    [InlineData(AgentKind.Cursor, "{}", false)]
    [InlineData(AgentKind.Cursor, "not-json", false)]
    [InlineData(AgentKind.OpenClaw, "{\"openclawAuth\":{\"accessToken\":\"x\"}}", false)]
    [InlineData(AgentKind.OpenClaw, "{\"tokens\":{}}", false)]
    [InlineData(AgentKind.OpenClaw, "{\"version\":1,\"profiles\":{}}", false)]
    [InlineData(AgentKind.OpenClaw, "{}", false)]
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
    public void Validate_RejectsCursorPayloadLargerThan64KiB()
    {
        var json = "{\"accessToken\":\"" + new string('x', ProviderCredentialValidator.MaxBytes) + "\",\"refreshToken\":\"r\"}";

        Assert.False(ProviderCredentialValidator.Validate(AgentKind.Cursor, json));
    }

    [Fact]
    public void Validate_RejectsOpenClawPayloadLargerThan64KiB()
    {
        var json = "{\"version\":1,\"profiles\":{\"anthropic:default\":{\"type\":\"api_key\",\"provider\":\"anthropic\",\"key\":\"" +
            new string('x', ProviderCredentialValidator.MaxBytes) + "\"}}}";

        Assert.False(ProviderCredentialValidator.Validate(AgentKind.OpenClaw, json));
    }

    [Fact]
    public void Validate_InvalidJsonDoesNotExposeCredentialValue()
    {
        const string secret = "credential-value-must-never-be-exposed";
        var exception = Record.Exception(() => ProviderCredentialValidator.Validate(AgentKind.Codex, $"{{\"tokens\":\"{secret}"));

        Assert.Null(exception);
    }
}
