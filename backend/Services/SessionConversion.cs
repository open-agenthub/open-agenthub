using AgentHub.Api.Models;
using AgentHub.Api.Persistence;

namespace AgentHub.Api.Services;

/// <summary>
/// The rules of turning an autonomous session into an interactive one, kept cluster-free so
/// they can be tested without Kubernetes (docs/session-mode-conversion.md). The service applies
/// the plan to the record and then runs the ordinary resume; nothing here touches a pod.
/// </summary>
public static class SessionConversion
{
    public const string InteractiveTarget = "interactive";

    public sealed record Plan(string UiMode, bool AutoApprove);

    /// <summary>
    /// Validates a conversion against the record and the live phase. Throws
    /// <see cref="ArgumentException"/> for a request that could never be right (unknown target
    /// mode, a UI mode the agent does not support) and <see cref="InvalidOperationException"/>
    /// for a session that is not in a state to be converted — the controllers map those to 400
    /// and 409, so a caller can tell "fix the request" from "wait or pause first".
    /// </summary>
    public static Plan Validate(SessionRecord record, string phase, ConvertSessionRequest request)
    {
        var target = string.IsNullOrWhiteSpace(request.Mode)
            ? InteractiveTarget
            : request.Mode.Trim().ToLowerInvariant();
        if (target != InteractiveTarget)
            throw new ArgumentException("A session can only be converted to 'interactive'.");

        if (record.Mode == SessionMode.Interactive)
            throw new InvalidOperationException("The session is already interactive.");
        if (record.Mode == SessionMode.Scheduled)
            throw new InvalidOperationException(
                "A scheduled session has no single conversation to continue; each run is its own.");
        // A running pod is still working on the autonomous run. Converting underneath it would
        // fork the conversation: the pod keeps writing its state over the key the resumed pod
        // reads from, and the next upload silently discards one of the two branches.
        if (!SessionStatus.CanConvertToInteractive(record.Mode, phase))
            throw new InvalidOperationException(
                "Pause the session or wait for it to finish before converting it.");

        var uiMode = SessionUiMode.NormalizeForCreate(request.UiMode, record.Agent, SessionMode.Interactive);
        return new Plan(uiMode, CreateSessionRequest.AutoApproveFor(request.AutoApprove, SessionMode.Interactive));
    }

    /// <summary>Rewrites the record; the caller persists it and resumes.</summary>
    public static void Apply(SessionRecord record, Plan plan)
    {
        record.ConvertedFrom = record.Mode;
        record.Mode = SessionMode.Interactive;
        record.UiMode = plan.UiMode;
        record.AutoApprove = plan.AutoApprove;
    }
}
