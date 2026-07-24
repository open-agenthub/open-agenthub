namespace AgentHub.Api.Permissions;

/// <summary>Rewrites an out-of-band permission prompt once it can no longer be answered
/// (expired) — e.g. removes the buttons and says "answer in the web terminal".</summary>
public interface IPermissionPromptEditor
{
    string Platform { get; }
    Task MarkExpiredAsync(PermissionRequest request, CancellationToken ct = default);
    /// <summary>The request was decided on another surface (e.g. the web app):
    /// reflect the decision and drop the buttons so the prompt doesn't look alive.</summary>
    Task MarkDecidedAsync(PermissionRequest request, string decision, CancellationToken ct = default);
}
