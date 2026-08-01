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

internal sealed class InMemoryMcpServerStore : IMcpServerStore
{
    private readonly Dictionary<string, McpServerRecord> _items = new();
    private int _next;

    public McpServerRecord Add(string owner, string name, string configJson = "{\"type\":\"http\",\"url\":\"https://example.test\"}")
    {
        var record = new McpServerRecord
        {
            Id = $"mcp-{++_next}", Owner = owner, Name = name, ConfigJson = configJson
        };
        _items[record.Id] = record;
        return record;
    }

    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<McpServerRecord> CreateAsync(string owner, SaveMcpServerRequest request, CancellationToken ct = default)
    {
        var record = new McpServerRecord
        {
            Id = $"mcp-{++_next}",
            Owner = owner,
            Name = LibraryValidation.ValidateMcpServerName(request.Name),
            Description = LibraryValidation.ValidateDescription(request.Description),
            ConfigJson = LibraryValidation.ValidateMcpServerConfig(request.ConfigJson)
        };
        _items[record.Id] = record;
        return Task.FromResult(record);
    }

    public Task<McpServerRecord> UpdateAsync(string owner, string id, SaveMcpServerRequest request, CancellationToken ct = default)
    {
        if (!_items.TryGetValue(id, out var record) || record.Owner != owner)
            throw new KeyNotFoundException();
        record.Name = LibraryValidation.ValidateMcpServerName(request.Name);
        record.Description = LibraryValidation.ValidateDescription(request.Description);
        record.ConfigJson = LibraryValidation.ValidateMcpServerConfig(request.ConfigJson);
        return Task.FromResult(record);
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
            ids.Where(_items.ContainsKey).Select(i => _items[i]).ToList());
}

internal sealed class InMemorySkillStore : ISkillStore
{
    private readonly Dictionary<string, SkillRecord> _items = new();
    private readonly Dictionary<string, string> _contents = new();
    private int _next;

    public SkillRecord Add(string owner, string name, string content = "# skill")
    {
        var record = new SkillRecord { Id = $"skill-{++_next}", Owner = owner, Name = name };
        _items[record.Id] = record;
        _contents[record.Id] = content;
        return record;
    }

    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<SkillRecord> CreateAsync(string owner, SaveSkillRequest request, CancellationToken ct = default)
    {
        var name = LibraryValidation.ValidateSkillName(request.Name);
        if (_items.Values.Any(i => i.Owner == owner && i.Name == name))
            throw new ArgumentException("You already have a skill with this name.");
        var record = new SkillRecord
        {
            Id = $"skill-{++_next}",
            Owner = owner,
            Name = name,
            Description = LibraryValidation.ValidateDescription(request.Description)
        };
        _items[record.Id] = record;
        _contents[record.Id] = LibraryValidation.ValidateSkillContent(request.Content);
        return Task.FromResult(record);
    }

    public Task<SkillRecord> UpdateAsync(string owner, string id, SaveSkillRequest request, CancellationToken ct = default)
    {
        if (!_items.TryGetValue(id, out var record) || record.Owner != owner)
            throw new KeyNotFoundException();
        record.Name = LibraryValidation.ValidateSkillName(request.Name);
        record.Description = LibraryValidation.ValidateDescription(request.Description);
        _contents[id] = LibraryValidation.ValidateSkillContent(request.Content);
        return Task.FromResult(record);
    }

    public Task DeleteAsync(string owner, string id, CancellationToken ct = default)
    {
        if (!_items.TryGetValue(id, out var record) || record.Owner != owner)
            throw new KeyNotFoundException();
        _items.Remove(id);
        _contents.Remove(id);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SkillRecord>> ListByOwnerAsync(string owner, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SkillRecord>>(
            _items.Values.Where(i => i.Owner == owner).OrderBy(i => i.Name).ToList());

    public Task<IReadOnlyList<SkillRecord>> GetManyAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SkillRecord>>(
            ids.Where(_items.ContainsKey).Select(i => _items[i]).ToList());

    public Task<string?> GetContentAsync(SkillRecord record, CancellationToken ct = default) =>
        Task.FromResult(_contents.GetValueOrDefault(record.Id));
}

internal sealed class InMemoryLibraryShareStore : ILibraryShareStore
{
    private readonly Dictionary<(string Type, string Id), (bool All, List<string> Users, List<string> Groups)> _shares = new();
    private readonly Dictionary<string, UserGroup> _groups = new();
    private int _next;

    public HashSet<string> KnownUsers { get; } = new(StringComparer.Ordinal);
    public bool UserSkillPublishing { get; set; }
    public List<(string Type, string Id)> DeletedItems { get; } = new();

    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<UserGroup>> ListGroupsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<UserGroup>>(_groups.Values.OrderBy(g => g.Name).ToList());

    public Task<UserGroup> CreateGroupAsync(string name, CancellationToken ct = default)
    {
        if (_groups.Values.Any(g => g.Name == name))
            throw new ArgumentException("A group with this name already exists.");
        var group = new UserGroup($"group-{++_next}", name, [], DateTime.UtcNow);
        _groups[group.Id] = group;
        return Task.FromResult(group);
    }

    public Task DeleteGroupAsync(string id, CancellationToken ct = default)
    {
        if (!_groups.Remove(id))
            throw new KeyNotFoundException();
        return Task.CompletedTask;
    }

    public Task<UserGroup> SetGroupMembersAsync(string id, IReadOnlyCollection<string> members, CancellationToken ct = default)
    {
        if (!_groups.TryGetValue(id, out var group))
            throw new KeyNotFoundException();
        foreach (var member in members)
        {
            if (!KnownUsers.Contains(member))
                throw new ArgumentException($"'{member}' is not a known user.");
        }
        var updated = group with { Members = members.ToList() };
        _groups[id] = updated;
        return Task.FromResult(updated);
    }

    public Task<LibraryShares> GetSharesAsync(string itemType, string itemId, CancellationToken ct = default)
    {
        var (all, users, groups) = _shares.GetValueOrDefault(
            (itemType, itemId), (false, new List<string>(), new List<string>()));
        return Task.FromResult(new LibraryShares(all, users, groups));
    }

    public Task<LibraryShares> SetSharesAsync(
        string itemType, string itemId, bool all,
        IReadOnlyCollection<string>? users, IReadOnlyCollection<string>? groups,
        string createdBy, CancellationToken ct = default)
    {
        var userList = (users ?? []).ToList();
        var groupList = (groups ?? []).ToList();
        foreach (var user in userList)
        {
            if (!KnownUsers.Contains(user))
                throw new ArgumentException($"'{user}' is not a known user.");
        }
        foreach (var group in groupList)
        {
            if (!_groups.ContainsKey(group))
                throw new ArgumentException($"Group '{group}' does not exist.");
        }
        _shares[(itemType, itemId)] = (all, userList, groupList);
        return Task.FromResult(new LibraryShares(all, userList, groupList));
    }

    public Task DeleteForItemAsync(string itemType, string itemId, CancellationToken ct = default)
    {
        _shares.Remove((itemType, itemId));
        DeletedItems.Add((itemType, itemId));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyCollection<string>> ListAccessibleItemIdsAsync(
        string itemType, string owner, CancellationToken ct = default)
    {
        var ids = new List<string>();
        foreach (var ((type, id), (all, users, groups)) in _shares)
        {
            if (type != itemType) continue;
            var viaGroup = groups.Any(g =>
                _groups.TryGetValue(g, out var grp) && grp.Members.Contains(owner));
            if (all || users.Contains(owner) || viaGroup)
                ids.Add(id);
        }
        return Task.FromResult<IReadOnlyCollection<string>>(ids);
    }

    public Task<bool> GetUserSkillPublishingAsync(CancellationToken ct = default) =>
        Task.FromResult(UserSkillPublishing);

    public Task SetUserSkillPublishingAsync(bool enabled, CancellationToken ct = default)
    {
        UserSkillPublishing = enabled;
        return Task.CompletedTask;
    }
}
