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
        ["anthropicApiKey"] = "anthropic_api_key",
        ["openAiApiKey"] = "openai_api_key",
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
        Put(data, "ssh_key", Normalize(credentials.SshPrivateKey));
        Put(data, "gitlab_token", credentials.GitlabToken);
        Put(data, "anthropic_api_key", credentials.AnthropicApiKey);
        Put(data, "openai_api_key", credentials.OpenAiApiKey);
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
        IDictionary<string, byte[]>? codexSubscription = null) => new()
    {
        SshPrivateKey = data.ContainsKey("ssh_key"),
        GitlabToken = data.ContainsKey("gitlab_token"),
        AnthropicApiKey = data.ContainsKey("anthropic_api_key"),
        OpenAiApiKey = data.ContainsKey("openai_api_key"),
        GitKnownHosts = data.ContainsKey("known_hosts"),
        GitUserName = data.ContainsKey("git_user_name"),
        GitUserEmail = data.ContainsKey("git_user_email"),
        ClaudeSubscription = claudeSubscription?.ContainsKey("credentials.json") == true,
        CodexSubscription = codexSubscription?.ContainsKey("auth.json") == true
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
            _ => throw new ArgumentException("Unsupported agent kind.", nameof(agent))
        };
        return Secret(name, @namespace, ownerLabelValue, new Dictionary<string, byte[]>
        {
            [fileName] = Encoding.UTF8.GetBytes(json)
        });
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
