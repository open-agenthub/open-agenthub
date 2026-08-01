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
        record.SecretJson = request.SecretJson;
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
