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
/// git PATs as id, kind and host; which API keys exist; and the stored session secrets by id, name
/// and delivery (docs/session-secrets.md). Never a secret's value.
/// </summary>
public sealed record RemoteCredentialListing(
    IReadOnlyDictionary<string, IReadOnlyList<ProviderAccountInfo>> Accounts,
    IReadOnlyList<GitPatInfo> GitPats,
    RemoteCredentialApiKeys ApiKeys,
    IReadOnlyList<SessionSecretInfo>? Secrets = null)
{
    public static RemoteCredentialListing From(
        IReadOnlyDictionary<string, IReadOnlyList<ProviderAccountInfo>> accounts, CredentialStatus status,
        IReadOnlyList<SessionSecretInfo>? secrets = null) =>
        new(accounts, status.GitPats, new RemoteCredentialApiKeys(status.AnthropicApiKey, status.OpenAiApiKey, status.CursorApiKey),
            secrets ?? Array.Empty<SessionSecretInfo>());
}
