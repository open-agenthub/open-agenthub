using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentHub.Api.Models;

namespace AgentHub.Api.Services;

/// <summary>
/// The accounts a provider secret holds, decoded. <see cref="Dirty"/> is set when reading had to
/// change anything — a legacy single-file layout migrated, an index entry without a file dropped,
/// a file without an index entry adopted — so the caller knows to write the secret back.
/// </summary>
public sealed class ProviderAccountSet
{
    public List<ProviderAccount> Accounts { get; } = new();
    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
    public bool Dirty { get; set; }

    public ProviderAccount? Find(string? id) =>
        id is null ? null : Accounts.FirstOrDefault(a => a.Id == id);

    public ProviderAccount? Default =>
        Accounts.FirstOrDefault(a => a.IsDefault) ?? Accounts.FirstOrDefault();
}

/// <summary>What <see cref="ProviderAccountSecret.Attach"/> decided for an upload.</summary>
public sealed record ProviderAccountAttachment(string AccountId, bool Created);

/// <summary>
/// Layout of a provider secret with several accounts, as pure operations on the secret's data
/// dictionary so the rules can be tested without a cluster. The layout, the lazy migration and
/// the attachment rules are explained in docs/provider-accounts.md.
/// </summary>
public static partial class ProviderAccountSecret
{
    public const string IndexKey = "accounts.json";
    public const string LegacyId = "default";
    public const int MaxLabelLength = 80;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    [GeneratedRegex("^[a-z0-9]{1,32}$")]
    private static partial Regex IdPattern();

    public static bool IsValidId(string? id) => id is not null && IdPattern().IsMatch(id);

    public static string FileName(AgentKind agent) => agent switch
    {
        AgentKind.Claude => "credentials.json",
        AgentKind.Codex => "auth.json",
        // Pinned from Cursor Agent CLI file store: auth.json (domain "cursor").
        AgentKind.Cursor => "auth.json",
        // Pinned from OpenClaw 2026.7.1-2: auth-profiles.json (logical JSON / SQLite store_json).
        AgentKind.OpenClaw => "auth-profiles.json",
        _ => throw new ArgumentException("Unsupported agent kind.", nameof(agent))
    };

    /// <summary>The secret key holding the file of one account.</summary>
    public static string Key(string accountId, AgentKind agent) => $"{accountId}.{FileName(agent)}";

    public static string NewId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();

    /// <summary>True when at least one login is stored, in either layout.</summary>
    public static bool HasAnyAccount(IDictionary<string, byte[]>? data, AgentKind agent)
    {
        if (data is null) return false;
        var file = FileName(agent);
        return data.ContainsKey(file) || data.Keys.Any(k => IsAccountKey(k, file));
    }

    /// <summary>
    /// Decodes a secret's data. A legacy single file becomes the account <see cref="LegacyId"/>;
    /// the index and the files are reconciled both ways, so a half-written secret degrades to a
    /// shorter listing instead of an exception on every session start.
    /// </summary>
    public static ProviderAccountSet Read(IDictionary<string, byte[]>? data, AgentKind agent, DateTime? now = null)
    {
        var set = new ProviderAccountSet();
        if (data is null) return set;
        var file = FileName(agent);
        var stamp = now ?? DateTime.UtcNow;

        if (data.TryGetValue(IndexKey, out var indexBytes))
        {
            try
            {
                var entries = JsonSerializer.Deserialize<List<ProviderAccount>>(indexBytes, Json) ?? new();
                foreach (var entry in entries)
                    if (IsValidId(entry.Id) && set.Find(entry.Id) is null) set.Accounts.Add(entry);
                if (entries.Count != set.Accounts.Count) set.Dirty = true;
            }
            catch (JsonException)
            {
                // The files are the truth; the index is rebuilt from them below.
                set.Dirty = true;
            }
        }

        foreach (var (key, value) in data)
        {
            if (!IsAccountKey(key, file)) continue;
            var id = key[..^(file.Length + 1)];
            set.Files[id] = value;
            if (set.Find(id) is null)
            {
                set.Accounts.Add(new ProviderAccount { Id = id, Label = id == LegacyId ? "Default" : id, CreatedAt = stamp });
                set.Dirty = true;
            }
        }

        // The single-file layout every secret had before accounts existed.
        if (data.TryGetValue(file, out var legacy))
        {
            if (!set.Files.ContainsKey(LegacyId))
            {
                set.Files[LegacyId] = legacy;
                if (set.Find(LegacyId) is null)
                    set.Accounts.Add(new ProviderAccount { Id = LegacyId, Label = "Default", CreatedAt = stamp, IsDefault = set.Accounts.Count == 0 });
            }
            set.Dirty = true;
        }

        var orphaned = set.Accounts.Where(a => !set.Files.ContainsKey(a.Id)).ToList();
        if (orphaned.Count > 0)
        {
            foreach (var account in orphaned) set.Accounts.Remove(account);
            set.Dirty = true;
        }

        if (set.Accounts.Count > 0 && !set.Accounts.Any(a => a.IsDefault))
        {
            set.Accounts[0].IsDefault = true;
            set.Dirty = true;
        }
        return set;
    }

    /// <summary>Encodes a set as secret data: the index plus one key per account.</summary>
    public static Dictionary<string, byte[]> Write(ProviderAccountSet set, AgentKind agent)
    {
        var data = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [IndexKey] = JsonSerializer.SerializeToUtf8Bytes(set.Accounts, Json)
        };
        foreach (var account in set.Accounts)
            if (set.Files.TryGetValue(account.Id, out var file)) data[Key(account.Id, agent)] = file;
        return data;
    }

    /// <summary>The account a session should mount: the one it names, else the default, else none.</summary>
    public static string? ResolveId(ProviderAccountSet set, string? credentialId) =>
        set.Find(credentialId)?.Id ?? set.Default?.Id;

    /// <summary>
    /// Decides which account an uploaded file belongs to and stores it there. The order of the
    /// rules, and why each exists, is in docs/provider-accounts.md ("How a login inside a session
    /// becomes a new account").
    /// </summary>
    public static ProviderAccountAttachment Attach(ProviderAccountSet set, byte[] file,
        ProviderAccountIdentity? identity, string? mountedId, DateTime? now = null)
    {
        var stamp = now ?? DateTime.UtcNow;
        identity = identity is { IsEmpty: true } ? null : identity;
        var mounted = set.Find(mountedId);

        if (mounted is not null)
        {
            var sameLogin = identity?.Key is null || mounted.Identity?.Key is null || mounted.Identity.Key == identity.Key;
            if (sameLogin) return Update(set, mounted, file, identity, stamp);
        }

        if (identity?.Key is not null)
        {
            var byIdentity = set.Accounts.FirstOrDefault(a => a.Identity?.Key == identity.Key);
            if (byIdentity is not null) return Update(set, byIdentity, file, identity, stamp);
        }

        if (mounted is null && identity is null)
        {
            var sameBytes = set.Accounts.FirstOrDefault(a =>
                set.Files.TryGetValue(a.Id, out var existing) && existing.AsSpan().SequenceEqual(file));
            if (sameBytes is not null) return Update(set, sameBytes, file, identity, stamp);
        }

        var id = NewId();
        while (set.Find(id) is not null) id = NewId();
        set.Accounts.Add(new ProviderAccount
        {
            Id = id,
            Label = DefaultLabel(identity, set.Accounts.Count + 1),
            Identity = identity,
            CreatedAt = stamp,
            LastUsedAt = stamp,
            IsDefault = set.Accounts.Count == 0
        });
        set.Files[id] = file;
        set.Dirty = true;
        return new ProviderAccountAttachment(id, Created: true);
    }

    /// <summary>Removes one account. The default moves to the first remaining one.</summary>
    public static bool Remove(ProviderAccountSet set, string id)
    {
        var account = set.Find(id);
        if (account is null) return false;
        set.Accounts.Remove(account);
        set.Files.Remove(id);
        if (account.IsDefault && set.Accounts.Count > 0) set.Accounts[0].IsDefault = true;
        set.Dirty = true;
        return true;
    }

    public static void MakeDefault(ProviderAccountSet set, string id)
    {
        foreach (var account in set.Accounts) account.IsDefault = account.Id == id;
        set.Dirty = true;
    }

    public static string NormalizeLabel(string? label)
    {
        var trimmed = (label ?? "").Trim();
        if (trimmed.Length == 0) throw new ArgumentException("A label is required.");
        if (trimmed.Length > MaxLabelLength)
            throw new ArgumentException($"The label is limited to {MaxLabelLength} characters.");
        if (trimmed.Any(char.IsControl)) throw new ArgumentException("The label must not contain control characters.");
        return trimmed;
    }

    private static ProviderAccountAttachment Update(ProviderAccountSet set, ProviderAccount account, byte[] file,
        ProviderAccountIdentity? identity, DateTime stamp)
    {
        set.Files[account.Id] = file;
        // An identity learned later is kept; one already known is refreshed from the newer file.
        if (identity is not null)
        {
            var hadNoLabelOfItsOwn = account.Label == account.Id || account.Label == "Default" && account.Identity is null;
            account.Identity = identity;
            if (hadNoLabelOfItsOwn && DefaultLabel(identity, 0) is { } better && better != account.Label)
                account.Label = better;
        }
        account.LastUsedAt = stamp;
        set.Dirty = true;
        return new ProviderAccountAttachment(account.Id, Created: false);
    }

    private static string DefaultLabel(ProviderAccountIdentity? identity, int ordinal)
    {
        var candidate = identity?.Email ?? identity?.Organization;
        if (!string.IsNullOrWhiteSpace(candidate))
            return candidate.Length > MaxLabelLength ? candidate[..MaxLabelLength] : candidate;
        return ordinal > 0 ? $"Account {ordinal}" : "Default";
    }

    private static bool IsAccountKey(string key, string file) =>
        key.Length > file.Length + 1 && key.EndsWith("." + file, StringComparison.Ordinal)
        && IsValidId(key[..^(file.Length + 1)]);

    public static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static byte[] Bytes(string json) => Encoding.UTF8.GetBytes(json);
}
