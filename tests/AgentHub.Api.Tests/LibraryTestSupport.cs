using AgentHub.Api.Library;

namespace AgentHub.Api.Tests;

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
        var record = new McpServerRecord
        {
            Id = $"mcp-{++_next}",
            Owner = owner,
            Name = LibraryValidation.ValidateMcpServerName(request.Name),
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
        record.Name = LibraryValidation.ValidateMcpServerName(request.Name);
        record.Description = LibraryValidation.ValidateDescription(request.Description);
        record.Kind = kind;
        record.ConfigJson = LibraryValidation.ValidateMcpServerConfig(request.ConfigJson, kind);
        record.SecretJson = request.SecretJson;
        record.UpdatedAt = DateTime.UtcNow;
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
            ids.Where(_items.ContainsKey).Select(i => _items[i]).OrderBy(i => i.Name).ToList());
}
