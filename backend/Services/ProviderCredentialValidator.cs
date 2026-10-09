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
                // Pinned from OpenClaw 2026.7.1-2 auth-profiles store (logical JSON / SQLite store_json):
                // { version?, profiles: { ... }, order?: { ... } } with at least one profile.
                AgentKind.OpenClaw => root.TryGetProperty("profiles", out var profiles) &&
                    profiles.ValueKind == JsonValueKind.Object &&
                    profiles.EnumerateObject().Any(),
                // Pinned from OpenCode 1.18.34 (packages/opencode/src/auth): a map keyed by provider
                // id, each entry { type: "api" | "oauth" | "wellknown", ... }. At least one entry,
                // and every entry typed — an empty map is what a logout leaves behind, and storing
                // it would replace a working login with nothing.
                AgentKind.OpenCode => root.EnumerateObject().Any() &&
                    root.EnumerateObject().All(entry =>
                        entry.Value.ValueKind == JsonValueKind.Object &&
                        entry.Value.TryGetProperty("type", out var type) &&
                        type.ValueKind == JsonValueKind.String &&
                        type.GetString() is "api" or "oauth" or "wellknown"),
                _ => false
            };
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
