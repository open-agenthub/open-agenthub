using System.Text;
using System.Text.Json;
using AgentHub.Api.Models;
using k8s.Models;

namespace AgentHub.Api.Services;

/// <summary>Creates the Kubernetes secrets that hold write-only user and provider credentials.</summary>
public static class CredentialSecretFactory
{
    private const string OwnerLabel = "agenthub.dev/owner";

    private static readonly IReadOnlyDictionary<string, string> CredentialKeys = new Dictionary<string, string>
    {
        ["sshPrivateKey"] = "ssh_key",
        ["gitlabToken"] = "gitlab_token",
        ["gitlabHost"] = "gitlab_host",
        ["githubToken"] = "github_token",
        ["githubHost"] = "github_host",
        ["anthropicApiKey"] = "anthropic_api_key",
        ["openAiApiKey"] = "openai_api_key",
        ["cursorApiKey"] = "cursor_api_key",
        ["gitKnownHosts"] = "known_hosts",
        ["gitUserName"] = "git_user_name",
        ["gitUserEmail"] = "git_user_email"
    };

    public static string CredentialKey(string propertyName) =>
        TryCredentialKey(propertyName, out var key)
            ? key
            : throw new ArgumentException("Unknown credential field.", nameof(propertyName));

    public static V1Secret CreateGeneralSecret(string name, string @namespace, string ownerLabelValue,
        IDictionary<string, byte[]>? existing, UserCredentials credentials)
    {
        var data = existing is null ? new Dictionary<string, byte[]>() : new Dictionary<string, byte[]>(existing);
        // Validated here rather than when the session starts: a token carrying a newline would add
        // an entry for an arbitrary host to the credential store, and a session that fails to start
        // hours later gives the user nothing to act on.
        ValidatePat(credentials.GitlabToken, credentials.GitlabHost, "GitLab");
        ValidatePat(credentials.GithubToken, credentials.GithubHost, "GitHub");
        Put(data, "ssh_key", Normalize(credentials.SshPrivateKey));
        Put(data, "gitlab_token", credentials.GitlabToken);
        Put(data, "gitlab_host", credentials.GitlabHost?.Trim());
        Put(data, "github_token", credentials.GithubToken);
        Put(data, "github_host", credentials.GithubHost?.Trim());
        Put(data, "anthropic_api_key", credentials.AnthropicApiKey);
        Put(data, "openai_api_key", credentials.OpenAiApiKey);
        Put(data, "cursor_api_key", credentials.CursorApiKey);
        Put(data, "known_hosts", credentials.GitKnownHosts);
        Put(data, "git_user_name", credentials.GitUserName);
        Put(data, "git_user_email", credentials.GitUserEmail);

        foreach (var field in credentials.Clear)
            if (TryCredentialKey(field, out var key))
                data.Remove(key);

        return Secret(name, @namespace, ownerLabelValue, data);
    }

    public static CredentialStatus CredentialStatus(IDictionary<string, byte[]> data,
        IDictionary<string, byte[]>? claudeSubscription = null,
        IDictionary<string, byte[]>? codexSubscription = null,
        IDictionary<string, byte[]>? cursorSubscription = null,
        IDictionary<string, byte[]>? openclawSubscription = null) => new()
    {
        SshPrivateKey = data.ContainsKey("ssh_key"),
        GitlabToken = data.ContainsKey("gitlab_token"),
        GitlabHost = data.ContainsKey("gitlab_host"),
        GithubToken = data.ContainsKey("github_token"),
        GithubHost = data.ContainsKey("github_host"),
        AnthropicApiKey = data.ContainsKey("anthropic_api_key"),
        OpenAiApiKey = data.ContainsKey("openai_api_key"),
        CursorApiKey = data.ContainsKey("cursor_api_key"),
        GitKnownHosts = data.ContainsKey("known_hosts"),
        GitUserName = data.ContainsKey("git_user_name"),
        GitUserEmail = data.ContainsKey("git_user_email"),
        ClaudeSubscription = claudeSubscription?.ContainsKey("credentials.json") == true,
        CodexSubscription = codexSubscription?.ContainsKey("auth.json") == true,
        // Pinned from Cursor Agent CLI file store: auth.json (domain "cursor").
        CursorSubscription = cursorSubscription?.ContainsKey("auth.json") == true,
        // Pinned from OpenClaw 2026.7.1-2: auth-profiles.json (logical JSON / SQLite store_json).
        OpenclawSubscription = openclawSubscription?.ContainsKey("auth-profiles.json") == true
    };

    public static V1Secret CreateProviderSecret(string name, string @namespace, string ownerLabelValue,
        AgentKind agent, string json)
    {
        if (!ProviderCredentialValidator.Validate(agent, json))
            throw new ArgumentException("Invalid provider credential document.", nameof(json));

        var fileName = agent switch
        {
            AgentKind.Claude => "credentials.json",
            AgentKind.Codex => "auth.json",
            // Pinned from Cursor Agent CLI file store: auth.json (domain "cursor").
            AgentKind.Cursor => "auth.json",
            // Pinned from OpenClaw 2026.7.1-2: auth-profiles.json (logical JSON / SQLite store_json).
            AgentKind.OpenClaw => "auth-profiles.json",
            _ => throw new ArgumentException("Unsupported agent kind.", nameof(agent))
        };
        return Secret(name, @namespace, ownerLabelValue, new Dictionary<string, byte[]>
        {
            [fileName] = Encoding.UTF8.GetBytes(json)
        });
    }

    private static void ValidatePat(string? token, string? host, string providerName)
    {
        if (!string.IsNullOrEmpty(token) && !ManualGitCredentials.IsValidToken(token))
            throw new ArgumentException(
                $"The {providerName} token must not contain whitespace or control characters and is "
                + $"limited to {ManualGitCredentials.MaxTokenLength} characters.");
        // The host is checked even without a token, so clearing one and keeping the other cannot
        // leave an unusable host behind that silently disables the credential later.
        if (!string.IsNullOrWhiteSpace(host) && !ManualGitCredentials.IsValidHost(host.Trim()))
            throw new ArgumentException(
                $"The {providerName} host must be a hostname with an optional port, without a "
                + "scheme or path.");
    }

    private static bool TryCredentialKey(string field, out string key) =>
        CredentialKeys.TryGetValue(JsonNamingPolicy.CamelCase.ConvertName(field), out key!);

    private static void Put(IDictionary<string, byte[]> data, string key, string? value)
    {
        if (!string.IsNullOrEmpty(value)) data[key] = Encoding.UTF8.GetBytes(value);
    }

    private static V1Secret Secret(string name, string @namespace, string ownerLabelValue, Dictionary<string, byte[]> data) => new()
    {
        Metadata = new V1ObjectMeta
        {
            Name = name,
            NamespaceProperty = @namespace,
            Labels = new Dictionary<string, string> { [OwnerLabel] = ownerLabelValue }
        },
        Type = "Opaque",
        Data = data
    };

    private static string? Normalize(string? pem) => pem is null ? null : pem.Replace("\r\n", "\n").TrimEnd() + "\n";
}
