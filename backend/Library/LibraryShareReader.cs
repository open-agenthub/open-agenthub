namespace AgentHub.Api.Library;

/// <summary>Item kinds a library share can point at.</summary>
public static class LibraryItemTypes
{
    public const string Mcp = "mcp";

    public static string Validate(string value) => value switch
    {
        Mcp => value,
        _ => throw new ArgumentException("Unknown library item type.")
    };
}

/// <summary>
/// Read side used by open-core access resolution: which shared items can this
/// user see? Only consulted when the enterprise license is active. EE replaces
/// the empty stub with a real share-matrix store.
/// </summary>
public interface ILibraryShareReader
{
    Task<IReadOnlyCollection<string>> ListAccessibleItemIdsAsync(
        string itemType, string owner, CancellationToken ct = default);
}

/// <summary>No-op share reader used when EE sharing is not wired.</summary>
public sealed class EmptyLibraryShareReader : ILibraryShareReader
{
    public Task<IReadOnlyCollection<string>> ListAccessibleItemIdsAsync(
        string itemType, string owner, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyCollection<string>>([]);
}
