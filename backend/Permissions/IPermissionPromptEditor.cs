namespace AgentHub.Api.Permissions;

/// <summary>Rewrites an out-of-band permission prompt once it can no longer be answered
/// (expired) — e.g. removes the buttons and says "answer in the web terminal".</summary>
public interface IPermissionPromptEditor
{
    string Platform { get; }
    Task MarkExpiredAsync(PermissionRequest request, CancellationToken ct = default);
}
