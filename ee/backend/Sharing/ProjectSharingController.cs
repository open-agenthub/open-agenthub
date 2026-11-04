// -----------------------------------------------------------------------------
// Open AgentHub Enterprise Edition — Session sharing.
// Part of the Enterprise Edition; NOT covered by the AGPL-3.0 license of the
// open-core. Source-available under the Open AgentHub Enterprise License
// (see ee/LICENSE); a valid subscription is required for production use.
// -----------------------------------------------------------------------------
using System.Security.Claims;
using AgentHub.Api.Ee.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentHub.Api.Ee.Sharing;

/// <summary>
/// The project settings' sharing surface (#409). Every action delegates to
/// <see cref="IProjectSharingService"/>, which holds the licence check; this class only
/// translates its exceptions into status codes, as <see cref="SharingController"/> does.
/// </summary>
[ApiController]
[Authorize]
[Route("api/ee/projects/{projectId}/shares")]
public sealed class ProjectSharingController(IProjectSharingService sharing) : ControllerBase
{
    private string Owner =>
        User.FindFirstValue("preferred_username")
        ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new UnauthorizedAccessException();

    [HttpGet]
    public Task<IActionResult> List(string projectId, CancellationToken ct)
        => Run(async () => Ok(await sharing.ListAsync(Owner, projectId, ct)));

    /// <summary>Adds a share or changes its role; one call for both, keyed by subject.</summary>
    [HttpPut]
    public Task<IActionResult> Upsert(string projectId, [FromBody] UpsertProjectShareRequest request, CancellationToken ct)
        => Run(async () => Ok(await sharing.ShareAsync(Owner, projectId, request.SubjectType, request.Subject, request.Role, ct)));

    [HttpDelete("{subjectType}/{subject}")]
    public Task<IActionResult> Delete(string projectId, string subjectType, string subject, CancellationToken ct)
        => Run(async () =>
        {
            if (!ProjectShareSubjects.TryParse(subjectType, out var type))
                return NotFound();
            await sharing.UnshareAsync(Owner, projectId, type, subject, ct);
            return NoContent();
        });

    /// <summary>
    /// Who the owner can name: signed-in users matching <paramref name="query"/>, the same lookup
    /// as for a session, and the owner's own IdP groups — the only groups a project can be shared
    /// with, so no other group name is ever disclosed.
    /// </summary>
    [HttpGet("recipients")]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting(SharingRecipientSearch.Policy)]
    public Task<IActionResult> FindRecipients(string projectId, [FromQuery] string? query,
        [FromServices] AgentHub.Api.Persistence.UserDirectory users,
        [FromServices] UserGroupStore groups,
        [FromServices] IConfiguration configuration, CancellationToken ct)
        => Run(async () =>
        {
            await sharing.ListAsync(Owner, projectId, ct);
            var identifier = query?.Trim() ?? "";
            var people = identifier.Length is > 0 and <= 254
                ? await users.FindShareRecipientsAsync(Owner, identifier,
                    configuration.GetValue("Sharing:UserSuggestionsEnabled", true), ct)
                : [];
            var ownGroups = await groups.GetGroupsAsync(Owner, ct);
            return Ok(new
            {
                users = people.Select(person => new { username = person.Owner, displayName = person.DisplayName }),
                groups = ownGroups
                    .Where(group => identifier.Length == 0 || group.Contains(identifier, StringComparison.OrdinalIgnoreCase))
            });
        });

    private async Task<IActionResult> Run(Func<Task<IActionResult>> action)
    {
        try
        {
            return await action();
        }
        catch (LicenseRequiredException exception)
        {
            return SharingResponses.LicenseRequired(this, exception);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (ArgumentException exception)
        {
            return SharingResponses.BadRequest(this, exception);
        }
    }
}
