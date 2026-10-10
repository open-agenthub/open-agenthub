using AgentHub.Api.Models;

namespace AgentHub.Api.Services;

/// <summary>
/// Pure, cluster-independent session status logic. Isolated here so the state
/// transitions (Running/Pending -> Paused, resumability of a paused session)
/// can be unit-tested without a Kubernetes cluster.
/// </summary>
public static class SessionStatus
{
    public const string Pending = "Pending";
    public const string Running = "Running";
    public const string Paused = "Paused";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
    public const string Scheduled = "Scheduled";

    /// <summary>Effective phase: a live pod's phase wins; otherwise the stored record status.</summary>
    public static string ResolvePhase(string? podPhase, string recordStatus)
        => string.IsNullOrEmpty(podPhase) ? recordStatus : podPhase;

    /// <summary>A finished or paused session (never a Scheduled one) can be resumed from saved state.</summary>
    public static bool CanResume(SessionMode mode, string phase)
        => mode != SessionMode.Scheduled && phase is Succeeded or Failed or Paused;

    /// <summary>A running or starting interactive/autonomous session can be paused (pod removed, state kept).</summary>
    public static bool CanPause(SessionMode mode, string phase)
        => mode != SessionMode.Scheduled && phase is Running or Pending;

    /// <summary>
    /// An autonomous session that could be resumed can instead be continued interactively
    /// (docs/session-mode-conversion.md). Built on <see cref="CanResume"/> rather than restated,
    /// because the conversion *is* a resume with a changed record: a phase one predicate accepts
    /// and the other refuses would offer a card whose button then answers 409.
    /// </summary>
    public static bool CanConvertToInteractive(SessionMode mode, string phase)
        => mode == SessionMode.Autonomous && CanResume(mode, phase);

    /// <summary>
    /// Whether saved state may be overwritten from outside. Only while no pod is live: a pod
    /// writes its own state over the same key when it stops, so an upload accepted next to a
    /// Running or Pending session is silently discarded at the next pause.
    /// </summary>
    public static bool CanReplaceState(string phase) => phase is not (Running or Pending);
}
