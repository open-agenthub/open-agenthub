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
}
