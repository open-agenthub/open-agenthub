using AgentHub.Api.Ee.Library;
using AgentHub.Api.Library;
using AgentHub.Api.Licensing;

namespace AgentHub.Api.Tests;

internal sealed class FakeEnterpriseLicense(bool enabled) : IEnterpriseLicense
{
    public LicenseStatus Status => new() { Valid = enabled, Present = enabled };
    public bool Enabled => enabled;
    public Task ReloadAsync(CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>Test double for <see cref="ILibraryShareReader"/> — returns configured ids.</summary>
internal sealed class FakeLibraryShareReader : ILibraryShareReader
{
    public HashSet<string> Ids { get; set; } = new(StringComparer.Ordinal);

    public Task<IReadOnlyCollection<string>> ListAccessibleItemIdsAsync(
        string itemType, string owner, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyCollection<string>>(Ids);
}

internal sealed class InMemoryMcpServerStore : IMcpServerStore
{
    private readonly Dictionary<string, McpServerRecord> _items = new();
    private int _next;

    public McpServerRecord Add(
        string owner,
        string name,
        string kind = "raw",
        string configJson = "{\"type\":\"http\",\"url\":\"https://example.test\"}",
        string? secretJson = null)
    {
        EnsureUniqueName(owner, name);
        var record = new McpServerRecord
        {
            Id = $"mcp-{++_next}",
            Owner = owner,
            Name = name,
            Kind = kind,
            ConfigJson = configJson,
            SecretJson = secretJson
        };
        _items[record.Id] = record;
        return record;
    }

    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<McpServerRecord> CreateAsync(string owner, SaveMcpServerRequest request, CancellationToken ct = default)
    {
        var kind = LibraryValidation.ValidateKind(request.Kind);
        var name = LibraryValidation.ValidateMcpServerName(request.Name);
        EnsureUniqueName(owner, name);
        var record = new McpServerRecord
        {
            Id = $"mcp-{++_next}",
            Owner = owner,
            Name = name,
            Description = LibraryValidation.ValidateDescription(request.Description),
            Kind = kind,
            ConfigJson = LibraryValidation.ValidateMcpServerConfig(request.ConfigJson, kind),
            SecretJson = request.SecretJson
        };
        _items[record.Id] = record;
        return Task.FromResult(record);
    }

    public Task<McpServerRecord> UpdateAsync(string owner, string id, SaveMcpServerRequest request, CancellationToken ct = default)
    {
        if (!_items.TryGetValue(id, out var record) || record.Owner != owner)
            throw new KeyNotFoundException();
        var kind = LibraryValidation.ValidateKind(request.Kind);
        var name = LibraryValidation.ValidateMcpServerName(request.Name);
        EnsureUniqueName(owner, name, exceptId: id);
        record.Name = name;
        record.Description = LibraryValidation.ValidateDescription(request.Description);
        record.Kind = kind;
        record.ConfigJson = LibraryValidation.ValidateMcpServerConfig(request.ConfigJson, kind);
        // null = leave unchanged; "" = clear; otherwise replace.
        if (request.SecretJson is not null)
            record.SecretJson = request.SecretJson.Length == 0 ? null : request.SecretJson;
        record.UpdatedAt = DateTime.UtcNow;
        return Task.FromResult(record);
    }

    private void EnsureUniqueName(string owner, string name, string? exceptId = null)
    {
        if (_items.Values.Any(i =>
                i.Owner == owner
                && (exceptId is null || i.Id != exceptId)
                && string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("An MCP server with this name already exists for this owner.");
        }
    }

    public Task DeleteAsync(string owner, string id, CancellationToken ct = default)
    {
        if (!_items.TryGetValue(id, out var record) || record.Owner != owner)
            throw new KeyNotFoundException();
        _items.Remove(id);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<McpServerRecord>> ListByOwnerAsync(string owner, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<McpServerRecord>>(
            _items.Values.Where(i => i.Owner == owner).OrderBy(i => i.Name).ToList());

    public Task<IReadOnlyList<McpServerRecord>> GetManyAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<McpServerRecord>>(
            ids.Where(_items.ContainsKey).Select(i => _items[i]).OrderBy(i => i.Name).ToList());
}

/// <summary>
/// In-memory share matrix for tests. Groups are IdP group names (same as
/// <c>UserGroupStore</c>), not a parallel custom-group system.
/// </summary>
internal sealed class InMemoryLibraryShareStore : ILibraryShareStore
{
    private readonly Dictionary<(string Type, string Id), (bool All, List<string> Users, List<string> Groups)> _shares = new();
    private readonly Dictionary<string, HashSet<string>> _memberships = new(StringComparer.Ordinal);

    public HashSet<string> KnownUsers { get; } = new(StringComparer.Ordinal);
    public HashSet<string> KnownGroups { get; } = new(StringComparer.Ordinal);
    public List<(string Type, string Id)> DeletedItems { get; } = new();

    public void AddMembership(string owner, string groupName)
    {
        KnownGroups.Add(groupName);
        if (!_memberships.TryGetValue(owner, out var groups))
            _memberships[owner] = groups = new HashSet<string>(StringComparer.Ordinal);
        groups.Add(groupName);
    }

    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<LibraryShares> GetSharesAsync(string itemType, string itemId, CancellationToken ct = default)
    {
        LibraryItemTypes.Validate(itemType);
        var (all, users, groups) = _shares.GetValueOrDefault(
            (itemType, itemId), (false, new List<string>(), new List<string>()));
        return Task.FromResult(new LibraryShares(all, users, groups));
    }

    public Task<LibraryShares> SetSharesAsync(
        string itemType, string itemId, bool all,
        IReadOnlyCollection<string>? users, IReadOnlyCollection<string>? groups,
        string createdBy, CancellationToken ct = default)
    {
        LibraryItemTypes.Validate(itemType);
        var userList = Normalize(users);
        var groupList = Normalize(groups);
        foreach (var user in userList)
        {
            if (!KnownUsers.Contains(user))
                throw new ArgumentException($"'{user}' is not a known user.");
        }
        foreach (var group in groupList)
        {
            if (!KnownGroups.Contains(group))
                throw new ArgumentException($"Group '{group}' does not exist.");
        }
        _shares[(itemType, itemId)] = (all, userList, groupList);
        return Task.FromResult(new LibraryShares(all, userList, groupList));
    }

    public Task DeleteForItemAsync(string itemType, string itemId, CancellationToken ct = default)
    {
        LibraryItemTypes.Validate(itemType);
        _shares.Remove((itemType, itemId));
        DeletedItems.Add((itemType, itemId));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyCollection<string>> ListAccessibleItemIdsAsync(
        string itemType, string owner, CancellationToken ct = default)
    {
        LibraryItemTypes.Validate(itemType);
        var memberOf = _memberships.GetValueOrDefault(owner) ?? [];
        var ids = new List<string>();
        foreach (var ((type, id), (all, users, groups)) in _shares)
        {
            if (type != itemType) continue;
            if (all || users.Contains(owner) || groups.Any(memberOf.Contains))
                ids.Add(id);
        }
        return Task.FromResult<IReadOnlyCollection<string>>(ids);
    }

    private static List<string> Normalize(IReadOnlyCollection<string>? values)
    {
        var result = new List<string>();
        foreach (var value in values ?? [])
        {
            var subject = value?.Trim();
            if (string.IsNullOrEmpty(subject))
                throw new ArgumentException("Empty entries are not allowed.");
            if (!result.Contains(subject, StringComparer.Ordinal))
                result.Add(subject);
        }
        return result;
    }
}
