namespace AgentHub.Api.Usage;

/// <summary>
/// One resource snapshot posted by a session pod. CPU and network are cumulative counters
/// since the pod started (cgroup cpu time, /proc/net/dev bytes); memory is a gauge. The
/// store turns the counters into per-session totals by adding the positive delta against
/// the previous snapshot — a counter that went backwards means a new pod (resume), whose
/// full value is the delta.
/// </summary>
public sealed record SessionResourceSample(double CpuSeconds, long MemoryBytes, long RxBytes, long TxBytes)
{
    public bool IsValid =>
        !double.IsNaN(CpuSeconds) && !double.IsInfinity(CpuSeconds) && CpuSeconds >= 0 &&
        MemoryBytes >= 0 && RxBytes >= 0 && TxBytes >= 0;
}
