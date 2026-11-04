// -----------------------------------------------------------------------------
// Open AgentHub Enterprise Edition — Session sharing.
// Part of the Enterprise Edition; NOT covered by the AGPL-3.0 license of the
// open-core. Source-available under the Open AgentHub Enterprise License
// (see ee/LICENSE); a valid subscription is required for production use.
// -----------------------------------------------------------------------------
using AgentHub.Api.Licensing;

namespace AgentHub.Api.Ee.Sharing;

/// <summary>The project half of <see cref="SessionShareStore"/>. Its own interface so the session
/// sharing fakes in tests and proofs need not grow members they never call.</summary>
public interface IProjectShareStore
{
    Task<IReadOnlyList<ProjectShare>> ListProjectSharesAsync(string owner, string projectId, CancellationToken ct = default);
    Task<ProjectShare> UpsertProjectShareAsync(string owner, string projectId, ProjectShareSubjectType subjectType, string subject, ShareRole role, CancellationToken ct = default);
    Task DeleteProjectShareAsync(string owner, string projectId, ProjectShareSubjectType subjectType, string subject, CancellationToken ct = default);
}

/// <summary>
/// What an owner can do to a project's shares (#409), behind the same licence check as session
/// sharing. A project share opens every session in the project, existing and future, through
/// <see cref="SessionAccessRules.Decide"/>; nothing is copied onto the sessions.
///
/// Errors as for <see cref="ISessionSharingService"/>: <see cref="LicenseRequiredException"/>,
/// <see cref="KeyNotFoundException"/> (not the caller's project, or no such share) and
/// <see cref="ArgumentException"/>s for invalid input.
/// </summary>
public interface IProjectSharingService
{
    Task<IReadOnlyList<ProjectShare>> ListAsync(string owner, string projectId, CancellationToken ct = default);
    Task<ProjectShare> ShareAsync(string owner, string projectId, ProjectShareSubjectType subjectType, string subject, ShareRole role, CancellationToken ct = default);
    Task UnshareAsync(string owner, string projectId, ProjectShareSubjectType subjectType, string subject, CancellationToken ct = default);
}

public sealed class ProjectSharingService(IProjectShareStore store, IEnterpriseLicense license)
    : IProjectSharingService
{
    public Task<IReadOnlyList<ProjectShare>> ListAsync(string owner, string projectId, CancellationToken ct = default)
    {
        RequireLicense();
        return store.ListProjectSharesAsync(owner, projectId, ct);
    }

    public Task<ProjectShare> ShareAsync(string owner, string projectId, ProjectShareSubjectType subjectType, string subject, ShareRole role, CancellationToken ct = default)
    {
        RequireLicense();
        return store.UpsertProjectShareAsync(owner, projectId, subjectType, subject, role, ct);
    }

    public Task UnshareAsync(string owner, string projectId, ProjectShareSubjectType subjectType, string subject, CancellationToken ct = default)
    {
        RequireLicense();
        return store.DeleteProjectShareAsync(owner, projectId, subjectType, subject, ct);
    }

    private void RequireLicense()
    {
        if (!license.Enabled) throw new LicenseRequiredException();
    }
}
