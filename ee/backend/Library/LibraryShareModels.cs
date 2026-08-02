// -----------------------------------------------------------------------------
// Open AgentHub Enterprise Edition — Library sharing (MCP catalog & skills).
// Part of the Enterprise Edition; NOT covered by the AGPL-3.0 license of the
// open-core. Source-available under the Open AgentHub Enterprise License
// (see ee/LICENSE); a valid subscription is required for production use.
// -----------------------------------------------------------------------------
using AgentHub.Api.Library;

namespace AgentHub.Api.Ee.Library;

/// <summary>Full store contract for library shares (read + write) and skill-publishing settings.</summary>
public interface ILibraryShareStore : ILibraryShareReader
{
    Task InitializeAsync(CancellationToken ct = default);
    Task<LibraryShares> GetSharesAsync(string itemType, string itemId, CancellationToken ct = default);
    Task<LibraryShares> SetSharesAsync(
        string itemType, string itemId, bool all,
        IReadOnlyCollection<string>? users, IReadOnlyCollection<string>? groups,
        string createdBy, CancellationToken ct = default);
    Task DeleteForItemAsync(string itemType, string itemId, CancellationToken ct = default);
    Task<bool> GetUserSkillPublishingAsync(CancellationToken ct = default);
    Task SetUserSkillPublishingAsync(bool enabled, CancellationToken ct = default);
}

/// <summary>Sharing state of one library item.</summary>
public sealed record LibraryShares(
    bool All,
    IReadOnlyList<string> Users,
    IReadOnlyList<string> Groups);

public sealed record LibrarySettings(bool UserSkillPublishing);

public sealed record UpdateSharesRequest(
    bool All,
    IReadOnlyList<string>? Users,
    IReadOnlyList<string>? Groups);

public sealed record UpdateLibrarySettingsRequest(bool UserSkillPublishing);
