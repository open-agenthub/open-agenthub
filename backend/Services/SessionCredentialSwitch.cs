using AgentHub.Api.Models;
using AgentHub.Api.Persistence;

namespace AgentHub.Api.Services;

/// <summary>
/// The rules for moving a live session to another provider account, kept cluster-free so they
/// are testable next to <see cref="SessionStatus"/>. The exceptions map onto HTTP in
/// <c>SessionsController</c>: ArgumentException → 400, InvalidOperationException → 409.
/// </summary>
public static class SessionCredentialSwitch
{
    public static void Validate(SessionRecord record, string phase, string? podIp, string? credentialId)
    {
        if (!ProviderAccountSecret.IsValidId(credentialId))
            throw new ArgumentException("A provider account id is required.");
        if (record.Mode == SessionMode.Scheduled)
            throw new ArgumentException("A scheduled session has no live pod to switch; edit the session instead.");
        // API-key sessions hold their key in the environment of a process that has already
        // started; there is no file to swap, and Auto (legacy) may be running on a key too.
        if (record.AuthMode != AgentAuthMode.Subscription)
            throw new ArgumentException("Only a Subscription session can switch its provider account.");
        if (phase != SessionStatus.Running || string.IsNullOrWhiteSpace(podIp))
            throw new InvalidOperationException("The session is not running; the account applies at the next start.");
    }
}
