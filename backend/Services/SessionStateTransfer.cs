namespace AgentHub.Api.Services;

/// <summary>
/// Shared limit for the two HTTP surfaces that move a session's provider state archive: the
/// web app through <c>api/sessions</c> and a workstation CLI through <c>api/remote</c>.
/// </summary>
public static class SessionStateTransfer
{
    /// <summary>
    /// A state archive is an entire agent home directory, and the transcript of a long session
    /// runs to tens of megabytes. Kestrel's 30 MB default would reject such an upload before
    /// any handler saw it, with nothing in the response to say that size was the reason.
    /// </summary>
    public const long MaxArchiveBytes = 200L * 1024 * 1024;
}
