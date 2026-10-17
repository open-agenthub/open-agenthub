using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentHub.Api.Models;

namespace AgentHub.Api.Services;

/// <summary>
/// A user's named secrets, in the Kubernetes secret <c>usersecrets-&lt;owner&gt;</c>
/// (docs/session-secrets.md).
///
/// The metadata is one JSON document under <see cref="IndexKey"/>; every value has a key of its
/// own (<c>value.&lt;id&gt;</c>), as raw bytes. Values inside the JSON document would have been one
/// key less, but JSON escaping can grow a value with control characters to six times its size,
/// and the 1 MiB a Kubernetes secret may hold would then be reached long before the limits below
/// say it should be.
///
/// The secret is never mounted into a pod. A session gets its own copy holding only what it
/// selected (<see cref="SessionSecretDeliveryFactory"/>), so a session that should see one secret
/// cannot list the others in a mounted directory.
/// </summary>
public static class SessionSecretStore
{
    public const string IndexKey = "index";
    private const string ValuePrefix = "value.";

    /// <summary>How many secrets a user may hold. With <see cref="MaxValueBytes"/> this keeps the
    /// whole secret at half of the 1 MiB Kubernetes allows, so a full store can still be written.</summary>
    public const int MaxEntries = 32;
    /// <summary>Enough for a service-account key file or a kubeconfig; a secret bigger than this
    /// is a file that belongs in the workspace, not in an environment variable.</summary>
    public const int MaxValueBytes = 16 * 1024;
    public const int MaxNameLength = 64;
    public const int MaxDescriptionLength = 500;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // A name is also the key of the session's own secret, so it must fit Kubernetes' key pattern
    // ([-._a-zA-Z0-9]+). A file name may not start with a dot: the kubelet keeps its own "..data"
    // links and timestamped directories in a secret mount, and "." or ".." would escape the file.
    private static readonly Regex EnvName = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);
    private static readonly Regex FileName = new("^[A-Za-z0-9_][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Variables a secret may not set. Everything the runtime sets itself is here — an override
    /// would replace the callback token, the presigned upload urls or the home directory under the
    /// session agent, which is PID 1 and holds them. So are the variables that change which code a
    /// process the runtime starts loads (the dynamic loader, node, git, the shell's startup file):
    /// the session agent and the approval hooks are those processes, and the hooks are what an
    /// interactive session's tool approvals rest on.
    /// </summary>
    public static readonly IReadOnlySet<string> ReservedEnvNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "HOME", "PATH", "USER", "LOGNAME", "SHELL", "TERM", "PWD", "OLDPWD", "HOSTNAME", "SHLVL",
        "TMPDIR", "TMP", "TEMP", "LANG", "LANGUAGE", "TZ",
        "IFS", "ENV", "BASH_ENV", "CDPATH", "PROMPT_COMMAND", "PS1", "PS2", "PS3", "PS4"
    };

    /// <summary>Prefixes a secret's variable may not start with; see <see cref="ReservedEnvNames"/>.
    /// The provider prefixes are reserved as a whole, not only the API-key names the runtime sets
    /// today: a variable such as ANTHROPIC_BASE_URL redirects where the CLI sends the login it was
    /// given, and a secret request is exactly the place an agent could ask for one.</summary>
    public static readonly IReadOnlyList<string> ReservedEnvPrefixes =
    [
        "AGENTHUB_", "LD_", "DYLD_", "NODE_", "NPM_CONFIG_", "GIT_", "SSH_", "LC_", "XDG_",
        "KUBERNETES_", "OTEL_", "ANTHROPIC_", "CLAUDE_", "CODEX_", "OPENAI_", "CURSOR_",
        "OPENCODE_", "OPENCLAW_"
    ];

    public sealed record Entry(string Id, string Name, string Delivery, string? Description, DateTime UpdatedAt);

    public static SessionSecretInfo Info(Entry e) => new(e.Id, e.Name, e.Delivery, e.Description, e.UpdatedAt);

    /// <summary>The stored entries whose value is present. Malformed metadata yields an empty list:
    /// it was validated when stored, so a bad document is corruption, and throwing here would stop
    /// every session of that user from starting.</summary>
    public static IReadOnlyList<Entry> Read(IDictionary<string, byte[]>? data)
    {
        if (data is null || !data.TryGetValue(IndexKey, out var raw)) return Array.Empty<Entry>();
        try
        {
            var parsed = JsonSerializer.Deserialize<List<Entry>>(raw, Json) ?? [];
            return parsed.Where(e => !string.IsNullOrEmpty(e.Id) && data.ContainsKey(ValueKey(e.Id))
                    && SessionSecretDelivery.IsValid(e.Delivery) && NameError(e.Name, e.Delivery) is null)
                .ToList();
        }
        catch (JsonException)
        {
            return Array.Empty<Entry>();
        }
    }

    /// <summary>The value of an entry; only the delivery into a session's own secret reads it.</summary>
    public static byte[]? Value(IDictionary<string, byte[]>? data, string id) =>
        data is not null && data.TryGetValue(ValueKey(id), out var value) ? value : null;

    /// <summary>
    /// Adds a secret, or updates the one stored under the same name (case-insensitively — FOO and
    /// foo side by side would be two secrets the person reading the list cannot tell apart). The id
    /// stays the same across an update, so project and session selections keep pointing at it.
    /// Validation is here, at the edge the owner is looking at: a session failing at start over a
    /// name Kubernetes refuses would give nobody anything to act on.
    /// </summary>
    public static Entry Upsert(IDictionary<string, byte[]> data, UpsertSessionSecretRequest request, DateTime now)
    {
        var delivery = request.Delivery?.Trim().ToLowerInvariant();
        if (!SessionSecretDelivery.IsValid(delivery))
            throw new ArgumentException("The delivery must be 'env' (environment variable) or 'file' (a file in ~/.secrets).");
        var name = request.Name?.Trim() ?? "";
        if (NameError(name, delivery!) is { } error) throw new ArgumentException(error);
        var description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        if (description is { Length: > MaxDescriptionLength })
            throw new ArgumentException($"The description is limited to {MaxDescriptionLength} characters.");

        byte[]? value = null;
        if (request.Value is not null)
        {
            if (request.Value.Length == 0)
                throw new ArgumentException("The value must not be empty.");
            if (request.Value.Contains('\0'))
                throw new ArgumentException("The value must not contain a NUL character.");
            value = Encoding.UTF8.GetBytes(request.Value);
            if (value.Length > MaxValueBytes)
                throw new ArgumentException($"A secret is limited to {MaxValueBytes / 1024} KiB.");
        }

        var entries = Read(data).ToList();
        var index = entries.FindIndex(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
        Entry entry;
        if (index >= 0)
        {
            entry = entries[index] with { Name = name, Delivery = delivery!, Description = description, UpdatedAt = now };
            entries[index] = entry;
        }
        else
        {
            if (value is null) throw new ArgumentException("A new secret needs a value.");
            if (entries.Count >= MaxEntries) throw new ArgumentException($"At most {MaxEntries} secrets can be stored.");
            entry = new Entry(Guid.NewGuid().ToString("N")[..16], name, delivery!, description, now);
            entries.Add(entry);
        }
        if (value is not null) data[ValueKey(entry.Id)] = value;
        WriteIndex(data, entries);
        return entry;
    }

    /// <summary>Removes a secret; false when no entry had that id. Idempotent either way.</summary>
    public static bool Remove(IDictionary<string, byte[]> data, string id)
    {
        var entries = Read(data);
        var kept = entries.Where(e => e.Id != id).ToList();
        data.Remove(ValueKey(id));
        if (kept.Count == entries.Count) return false;
        WriteIndex(data, kept);
        return true;
    }

    /// <summary>Why a name cannot be used for a delivery, or null when it can.</summary>
    public static string? NameError(string? name, string delivery)
    {
        if (string.IsNullOrEmpty(name)) return "A secret needs a name.";
        if (name.Length > MaxNameLength) return $"A secret's name is limited to {MaxNameLength} characters.";
        if (delivery == SessionSecretDelivery.Env)
        {
            if (!EnvName.IsMatch(name))
                return "An environment variable name may contain letters, digits and '_' and must not start with a digit.";
            if (IsReservedEnvName(name))
                return $"'{name}' is set by the session runtime and cannot be overridden by a secret.";
            return null;
        }
        return FileName.IsMatch(name)
            ? null
            : "A file name may contain letters, digits, '.', '_' and '-' and must not start with '.' or '-'.";
    }

    public static bool IsReservedEnvName(string name) =>
        ReservedEnvNames.Contains(name)
        || ReservedEnvPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    private static string ValueKey(string id) => ValuePrefix + id;

    private static void WriteIndex(IDictionary<string, byte[]> data, IReadOnlyList<Entry> entries)
    {
        if (entries.Count == 0) data.Remove(IndexKey);
        else data[IndexKey] = JsonSerializer.SerializeToUtf8Bytes(entries, Json);
    }
}
