using System.Text.Json.Serialization;

namespace AgentHub.Api.Models;

/// <summary>Which API keys are stored — presence only, never a value.</summary>
public sealed record RemoteCredentialApiKeys(
    bool Anthropic,
    [property: JsonPropertyName("openai")] bool OpenAi,
    bool Cursor);

/// <summary>
/// What an API or MCP caller can pick from when it creates a session (docs/credential-scopes.md):
/// provider accounts keyed exactly like <c>GET /api/credentials/accounts</c> (<c>Claude</c>,
/// <c>Codex</c>, …), so a client does not have to learn two spellings of one provider; the stored
/// git PATs as id, kind and host; and which API keys exist. Never a secret.
/// </summary>
public sealed record RemoteCredentialListing(
    IReadOnlyDictionary<string, IReadOnlyList<ProviderAccountInfo>> Accounts,
    IReadOnlyList<GitPatInfo> GitPats,
    RemoteCredentialApiKeys ApiKeys)
{
    public static RemoteCredentialListing From(
        IReadOnlyDictionary<string, IReadOnlyList<ProviderAccountInfo>> accounts, CredentialStatus status) =>
        new(accounts, status.GitPats, new RemoteCredentialApiKeys(status.AnthropicApiKey, status.OpenAiApiKey, status.CursorApiKey));
}
