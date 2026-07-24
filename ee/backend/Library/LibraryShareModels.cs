// -----------------------------------------------------------------------------
// Open AgentHub Enterprise Edition — Library sharing (MCP servers & skills).
// Part of the Enterprise Edition; NOT covered by the AGPL-3.0 license of the
// open-core. Source-available under the Open AgentHub Enterprise License
// (see ee/LICENSE); a valid subscription is required for production use.
// -----------------------------------------------------------------------------
namespace AgentHub.Api.Ee.Library;

/// <summary>Item kinds a library share can point at.</summary>
public static class LibraryItemTypes
{
    public const string Mcp = "mcp";
    public const string Skill = "skill";

    public static string Validate(string value) => value switch
    {
        Mcp or Skill => value,
        _ => throw new ArgumentException("Unknown library item type.")
    };
}

/// <summary>
/// Read side used by the open-core access resolution: which shared items can
/// this user see? Only consulted when the enterprise license is active.
/// </summary>
public interface ILibraryShareReader
{
    Task<IReadOnlyCollection<string>> ListAccessibleItemIdsAsync(
        string itemType, string owner, CancellationToken ct = default);
}

/// <summary>Full store contract for groups, item shares and library settings.</summary>
public interface ILibraryShareStore : ILibraryShareReader
{
    Task InitializeAsync(CancellationToken ct = default);
    Task<IReadOnlyList<UserGroup>> ListGroupsAsync(CancellationToken ct = default);
    Task<UserGroup> CreateGroupAsync(string name, CancellationToken ct = default);
    Task DeleteGroupAsync(string id, CancellationToken ct = default);
    Task<UserGroup> SetGroupMembersAsync(string id, IReadOnlyCollection<string> members, CancellationToken ct = default);
    Task<LibraryShares> GetSharesAsync(string itemType, string itemId, CancellationToken ct = default);
    Task<LibraryShares> SetSharesAsync(
        string itemType, string itemId, bool all,
        IReadOnlyCollection<string>? users, IReadOnlyCollection<string>? groups,
        string createdBy, CancellationToken ct = default);
    Task DeleteForItemAsync(string itemType, string itemId, CancellationToken ct = default);
    Task<bool> GetUserSkillPublishingAsync(CancellationToken ct = default);
    Task SetUserSkillPublishingAsync(bool enabled, CancellationToken ct = default);
}

public sealed record UserGroup(
    string Id,
    string Name,
    IReadOnlyList<string> Members,
    DateTime CreatedAt);

/// <summary>Sharing state of one library item.</summary>
public sealed record LibraryShares(
    bool All,
    IReadOnlyList<string> Users,
    IReadOnlyList<string> Groups);

public sealed record LibrarySettings(bool UserSkillPublishing);

public sealed record CreateGroupRequest(string Name);
public sealed record SetGroupMembersRequest(IReadOnlyList<string> Members);
public sealed record UpdateSharesRequest(
    bool All,
    IReadOnlyList<string>? Users,
    IReadOnlyList<string>? Groups);
public sealed record UpdateLibrarySettingsRequest(bool UserSkillPublishing);
