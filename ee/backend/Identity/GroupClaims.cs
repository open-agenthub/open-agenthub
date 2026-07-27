// -----------------------------------------------------------------------------
// Open AgentHub Enterprise Edition — OAuth/OIDC group-claim extraction.
// Part of the Enterprise Edition; NOT covered by the AGPL-3.0 license of the
// open-core. Source-available under the Open AgentHub Enterprise License
// (see ee/LICENSE); a valid subscription is required for production use.
// -----------------------------------------------------------------------------
using System.Security.Claims;
using System.Text.Json;

namespace AgentHub.Api.Ee.Identity;

/// <summary>
/// Reads the user's groups from the OIDC/OAuth token. The claim name is configurable via
/// <c>Ee:Groups:Claim</c> (default "groups", which Keycloak/Entra/Okta emit with the right
/// mapper). Handles both wire shapes: one claim per group (ASP.NET splits JSON arrays this
/// way) and a single claim whose value is a JSON array string.
/// </summary>
public static class GroupClaims
{
    public const string DefaultClaim = "groups";

    public static IReadOnlyList<string> Extract(ClaimsPrincipal user, string claimName = DefaultClaim)
        => Extract(user.FindAll(claimName).Select(c => c.Value));

    public static IReadOnlyList<string> Extract(IEnumerable<string> claimValues)
    {
        var groups = new List<string>();
        foreach (var value in claimValues)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            var trimmed = value.Trim();
            if (trimmed.StartsWith('['))
            {
                try
                {
                    var parsed = JsonSerializer.Deserialize<List<string>>(trimmed);
                    if (parsed is not null) { groups.AddRange(parsed); continue; }
                }
                catch (JsonException) { /* not a JSON array — treat as a literal name */ }
            }
            groups.Add(trimmed);
        }
        return groups
            .Select(Normalize)
            .Where(g => g.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Keycloak group paths come as "/team/sub"; store them without the leading slash.</summary>
    private static string Normalize(string group)
    {
        var g = group.Trim();
        return g.StartsWith('/') ? g[1..] : g;
    }
}
