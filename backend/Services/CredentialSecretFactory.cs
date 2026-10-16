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
        ["anthropicApiKey"] = "anthropic_api_key",
        ["openAiApiKey"] = "openai_api_key",
        ["cursorApiKey"] = "cursor_api_key",
        ["openCodeApiKey"] = "opencode_api_key",
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
        // Any write is the moment the legacy per-provider PAT slots are folded into the list and
        // dropped; reads only fold them in memory (see GitPatStore).
        if (GitPatStore.HasLegacySlots(data))
            GitPatStore.Write(data, GitPatStore.Read(data));
        Put(data, "ssh_key", Normalize(credentials.SshPrivateKey));
        Put(data, "anthropic_api_key", credentials.AnthropicApiKey);
        Put(data, "openai_api_key", credentials.OpenAiApiKey);
        Put(data, "cursor_api_key", credentials.CursorApiKey);
        Put(data, "opencode_api_key", credentials.OpenCodeApiKey);
        Put(data, "known_hosts", credentials.GitKnownHosts);
        Put(data, "git_user_name", credentials.GitUserName);
        Put(data, "git_user_email", credentials.GitUserEmail);

        foreach (var field in credentials.Clear)
            if (TryCredentialKey(field, out var key))
                data.Remove(key);

        return Secret(name, @namespace, ownerLabelValue, data);
    }

    /// <summary>Stores or rotates one PAT in the user's credential secret; see <see cref="GitPatStore.Upsert"/>.</summary>
    public static (V1Secret Secret, GitPatInfo Info) UpsertGitPat(string name, string @namespace, string ownerLabelValue,
        IDictionary<string, byte[]>? existing, UpsertGitPatRequest request)
    {
        var data = existing is null ? new Dictionary<string, byte[]>() : new Dictionary<string, byte[]>(existing);
        var entry = GitPatStore.Upsert(data, request);
        return (Secret(name, @namespace, ownerLabelValue, data), new GitPatInfo(entry.Id, entry.Kind, entry.Host));
    }

    /// <summary>Removes one PAT; the secret is returned unchanged in content when the id is unknown.</summary>
    public static V1Secret RemoveGitPat(string name, string @namespace, string ownerLabelValue,
        IDictionary<string, byte[]>? existing, string id)
    {
        var data = existing is null ? new Dictionary<string, byte[]>() : new Dictionary<string, byte[]>(existing);
        GitPatStore.Remove(data, id);
        return Secret(name, @namespace, ownerLabelValue, data);
    }

    public static CredentialStatus CredentialStatus(IDictionary<string, byte[]> data,
        IDictionary<string, byte[]>? claudeSubscription = null,
        IDictionary<string, byte[]>? codexSubscription = null,
        IDictionary<string, byte[]>? cursorSubscription = null,
        IDictionary<string, byte[]>? openclawSubscription = null,
        IDictionary<string, byte[]>? opencodeSubscription = null) => new()
    {
        SshPrivateKey = data.ContainsKey("ssh_key"),
        // Projected to id/kind/host: the status answer is the one place the list is read back,
        // and the token must not be in it.
        GitPats = GitPatStore.Read(data).Select(e => new GitPatInfo(e.Id, e.Kind, e.Host)).ToList(),
        AnthropicApiKey = data.ContainsKey("anthropic_api_key"),
        OpenAiApiKey = data.ContainsKey("openai_api_key"),
        CursorApiKey = data.ContainsKey("cursor_api_key"),
        OpenCodeApiKey = data.ContainsKey("opencode_api_key"),
        GitKnownHosts = data.ContainsKey("known_hosts"),
        GitUserName = data.ContainsKey("git_user_name"),
        GitUserEmail = data.ContainsKey("git_user_email"),
        // Either layout counts: the bare file a secret had before accounts existed, or any
        // <accountId>.<file> key of the layout described in docs/provider-accounts.md.
        ClaudeSubscription = ProviderAccountSecret.HasAnyAccount(claudeSubscription, AgentKind.Claude),
        CodexSubscription = ProviderAccountSecret.HasAnyAccount(codexSubscription, AgentKind.Codex),
        CursorSubscription = ProviderAccountSecret.HasAnyAccount(cursorSubscription, AgentKind.Cursor),
        OpenclawSubscription = ProviderAccountSecret.HasAnyAccount(openclawSubscription, AgentKind.OpenClaw),
        OpencodeSubscription = ProviderAccountSecret.HasAnyAccount(opencodeSubscription, AgentKind.OpenCode)
    };

    /// <summary>
    /// A provider secret holding exactly one account. Kept for callers that store a login without
    /// knowing about accounts; the result is the migrated layout, not the legacy single file, so
    /// a secret this writes never needs migrating.
    /// </summary>
    public static V1Secret CreateProviderSecret(string name, string @namespace, string ownerLabelValue,
        AgentKind agent, string json)
    {
        if (!ProviderCredentialValidator.Validate(agent, json))
            throw new ArgumentException("Invalid provider credential document.", nameof(json));

        var set = new ProviderAccountSet();
        ProviderAccountSecret.Attach(set, Encoding.UTF8.GetBytes(json), identity: null, mountedId: null);
        return ProviderSecret(name, @namespace, ownerLabelValue, ProviderAccountSecret.Write(set, agent));
    }

    /// <summary>A provider secret from already-encoded account data (see <see cref="ProviderAccountSecret.Write"/>).</summary>
    public static V1Secret ProviderSecret(string name, string @namespace, string ownerLabelValue,
        Dictionary<string, byte[]> data) => Secret(name, @namespace, ownerLabelValue, data);

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
