using AgentHub.Api.Ee.Library;
using AgentHub.Api.Library;
using AgentHub.Api.Licensing;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentHub.Api.Tests;

internal static class LibraryTest
{
    /// <summary>Search service without embeddings — pure FTS via the store fake.</summary>
    public static SkillSearchService SearchService(ISkillStore skills) => new(
        skills,
        new InMemorySkillEmbeddingStore(),
        new NullEmbeddingProvider(),
        NullLogger<SkillSearchService>.Instance);
}

internal sealed class InMemoryProjectStore : IProjectStore
{
    private readonly Dictionary<(string Owner, string Id), ProjectInfo> _items = new();
    private int _next;

    public ProjectInfo Add(string owner, string name = "Project")
    {
        var info = new ProjectInfo($"project-{++_next}", name, null, 0);
        _items[(owner, info.Id)] = info;
        return info;
    }

    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<ProjectInfo>> ListAsync(string owner, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ProjectInfo>>(
            _items.Where(kv => kv.Key.Owner == owner).Select(kv => kv.Value).ToList());

    public Task<ProjectInfo?> GetAsync(string owner, string id, CancellationToken ct = default) =>
        Task.FromResult(_items.GetValueOrDefault((owner, id)));

    public Task<ProjectInfo> CreateAsync(string owner, CreateProjectRequest request, CancellationToken ct = default) =>
        Task.FromResult(Add(owner, request.Name));

    public Task<ProjectInfo?> UpdateAsync(string owner, string id, UpdateProjectRequest request, CancellationToken ct = default) =>
        Task.FromResult(_items.GetValueOrDefault((owner, id)));

    public Task<bool> DeleteAsync(string owner, string id, CancellationToken ct = default) =>
        Task.FromResult(_items.Remove((owner, id)));
}

internal sealed class InMemorySkillEmbeddingStore : ISkillEmbeddingStore
{
    private readonly Dictionary<string, (string Model, float[] Vector)> _items = new();

    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task UpsertAsync(string skillId, string model, float[] vector, CancellationToken ct = default)
    {
        _items[skillId] = (model, vector);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string skillId, CancellationToken ct = default)
    {
        _items.Remove(skillId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<string, float[]>> GetManyAsync(
        IReadOnlyCollection<string> skillIds, string model, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<string, float[]>>(skillIds
            .Where(id => _items.TryGetValue(id, out var e) && e.Model == model)
            .ToDictionary(id => id, id => _items[id].Vector));
}

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
            SecretJson = string.IsNullOrEmpty(request.SecretJson) ? null : request.SecretJson
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

internal sealed class InMemorySkillStore : ISkillStore
{
    private readonly Dictionary<string, SkillRecord> _items = new();
    private readonly Dictionary<(string Id, int Version), string> _contents = new();
    private readonly Dictionary<(string Id, int Version), IReadOnlyList<SkillFile>> _files = new();
    private readonly Dictionary<string, List<SkillVersionRecord>> _versions = new();
    private int _next;

    public SkillRecord Add(string owner, string name, string content = "# skill", string? projectId = null,
        IReadOnlyList<SkillFile>? files = null)
    {
        var record = new SkillRecord
        {
            Id = $"skill-{++_next}", Owner = owner, Name = name, ProjectId = projectId
        };
        _items[record.Id] = record;
        _contents[(record.Id, 1)] = content;
        _files[(record.Id, 1)] = files ?? [];
        _versions[record.Id] =
            [new SkillVersionRecord { SkillId = record.Id, Version = 1, Name = name, CreatedBy = owner }];
        return record;
    }

    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<SkillRecord> CreateAsync(string owner, SaveSkillRequest request, CancellationToken ct = default)
    {
        var name = LibraryValidation.ValidateSkillName(request.Name);
        if (_items.Values.Any(i => i.Owner == owner && i.Name == name && i.ProjectId == request.ProjectId))
            throw new ArgumentException("A skill with this name already exists in this scope.");
        var record = new SkillRecord
        {
            Id = $"skill-{++_next}",
            Owner = owner,
            Name = name,
            Description = LibraryValidation.ValidateDescription(request.Description),
            ProjectId = request.ProjectId
        };
        _items[record.Id] = record;
        _contents[(record.Id, 1)] = LibraryValidation.ValidateSkillContent(request.Content);
        _files[(record.Id, 1)] = LibraryValidation.ValidateSkillFiles(request.Files) ?? [];
        _versions[record.Id] = [NewVersion(record, request, 1, request.SavedBy ?? owner)];
        return Task.FromResult(record);
    }

    public Task<SkillRecord> UpdateAsync(string owner, string id, SaveSkillRequest request, CancellationToken ct = default)
    {
        if (!_items.TryGetValue(id, out var record) || record.Owner != owner)
            throw new KeyNotFoundException();
        record.Name = LibraryValidation.ValidateSkillName(request.Name);
        record.Description = LibraryValidation.ValidateDescription(request.Description);
        var files = LibraryValidation.ValidateSkillFiles(request.Files)
            ?? _files.GetValueOrDefault((id, record.Version), []);
        record.Version++;
        _contents[(id, record.Version)] = LibraryValidation.ValidateSkillContent(request.Content);
        _files[(id, record.Version)] = files;
        _versions[id].Add(NewVersion(record, request, record.Version, request.SavedBy ?? owner));
        return Task.FromResult(record);
    }

    public Task DeleteAsync(string owner, string id, CancellationToken ct = default)
    {
        if (!_items.TryGetValue(id, out var record) || record.Owner != owner)
            throw new KeyNotFoundException();
        _items.Remove(id);
        _versions.Remove(id);
        foreach (var key in _contents.Keys.Where(k => k.Id == id).ToList()) _contents.Remove(key);
        foreach (var key in _files.Keys.Where(k => k.Id == id).ToList()) _files.Remove(key);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SkillRecord>> ListByOwnerAsync(string owner, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SkillRecord>>(
            _items.Values.Where(i => i.Owner == owner).OrderBy(i => i.Name).ToList());

    public Task<IReadOnlyList<SkillRecord>> GetManyAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SkillRecord>>(
            ids.Where(_items.ContainsKey).Select(i => _items[i]).ToList());

    public Task<string?> GetContentAsync(SkillRecord record, CancellationToken ct = default) =>
        Task.FromResult(_contents.GetValueOrDefault((record.Id, record.Version)));

    public Task<IReadOnlyList<SkillFile>> GetFilesAsync(string id, int version, CancellationToken ct = default) =>
        Task.FromResult(_files.GetValueOrDefault((id, version), []));

    public Task<IReadOnlyList<SkillVersionRecord>> ListVersionsAsync(string id, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SkillVersionRecord>>(
            _versions.GetValueOrDefault(id, []).OrderByDescending(v => v.Version).ToList());

    public Task<string?> GetVersionContentAsync(string id, int version, CancellationToken ct = default) =>
        Task.FromResult(_contents.GetValueOrDefault((id, version)));

    public async Task<SkillRecord> RestoreVersionAsync(
        string owner, string id, int version, string? savedBy = null, CancellationToken ct = default)
    {
        if (!_items.TryGetValue(id, out var record) || record.Owner != owner)
            throw new KeyNotFoundException();
        var content = _contents.GetValueOrDefault((id, version)) ?? throw new KeyNotFoundException();
        var target = _versions[id].First(v => v.Version == version);
        return await UpdateAsync(owner, id, new SaveSkillRequest(
            target.Name, target.Description, content,
            Comment: $"Restored version {version}.", SavedBy: savedBy ?? owner,
            Files: _files.GetValueOrDefault((id, version), [])), ct);
    }

    public Task<IReadOnlyList<(SkillRecord Record, double Rank)>> SearchAsync(
        IReadOnlyCollection<string> ids, string query, int limit, CancellationToken ct = default)
    {
        var q = query.ToLowerInvariant();
        var hits = ids
            .Where(_items.ContainsKey)
            .Select(i => _items[i])
            .Select(r => (Record: r, Text: $"{r.Name} {r.Description} {_contents.GetValueOrDefault((r.Id, r.Version), "")}".ToLowerInvariant()))
            .Where(x => x.Text.Contains(q))
            .Select(x => (x.Record, Rank: (double)CountOccurrences(x.Text, q)))
            .OrderByDescending(x => x.Rank)
            .Take(limit)
            .ToList();
        return Task.FromResult<IReadOnlyList<(SkillRecord, double)>>(hits);
    }

    private static int CountOccurrences(string text, string term)
    {
        int count = 0, index = 0;
        while ((index = text.IndexOf(term, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += term.Length;
        }
        return count;
    }

    private static SkillVersionRecord NewVersion(
        SkillRecord record, SaveSkillRequest request, int version, string savedBy) => new()
    {
        SkillId = record.Id,
        Version = version,
        Name = record.Name,
        Description = record.Description,
        CreatedBy = savedBy,
        Comment = LibraryValidation.ValidateComment(request.Comment)
    };
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
    public bool UserSkillPublishing { get; set; }
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

    public Task<bool> GetUserSkillPublishingAsync(CancellationToken ct = default) =>
        Task.FromResult(UserSkillPublishing);

    public Task SetUserSkillPublishingAsync(bool enabled, CancellationToken ct = default)
    {
        UserSkillPublishing = enabled;
        return Task.CompletedTask;
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
