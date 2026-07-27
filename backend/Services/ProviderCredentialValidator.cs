using System.Text;
using System.Text.Json;
using AgentHub.Api.Models;

namespace AgentHub.Api.Services;

/// <summary>Validates the provider-specific shape of a subscription credential file without reading credential values.</summary>
public static class ProviderCredentialValidator
{
    public const int MaxBytes = 64 * 1024;

    public static bool Validate(AgentKind agent, string json)
    {
        if (string.IsNullOrWhiteSpace(json) || Encoding.UTF8.GetByteCount(json) > MaxBytes)
            return false;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;

            return agent switch
            {
                AgentKind.Claude => root.TryGetProperty("claudeAiOauth", out var oauth) && oauth.ValueKind == JsonValueKind.Object,
                AgentKind.Codex => root.TryGetProperty("tokens", out var tokens) && tokens.ValueKind == JsonValueKind.Object,
                // Pinned from Cursor Agent CLI file store (2026.07.23-e383d2b): auth.json
                // shape is { accessToken, refreshToken, apiKey?, bedrockCredentials? }.
                AgentKind.Cursor => root.TryGetProperty("accessToken", out var token) &&
                    token.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrEmpty(token.GetString()),
                // PLACEHOLDER until Task 5 runtime discovery of the real OpenClaw auth file shape.
                AgentKind.OpenClaw => root.TryGetProperty("openclawAuth", out var auth) && auth.ValueKind == JsonValueKind.Object,
                _ => false
            };
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
