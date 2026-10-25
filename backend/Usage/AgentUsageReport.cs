namespace AgentHub.Api.Usage;

/// <summary>
/// A session's cumulative token usage as its session agent read it from the CLI's own files
/// (agent-runtime/common/usage.js). <see cref="OutputTokens"/> includes reasoning;
/// <see cref="CostUsd"/> is null when the CLI computes no cost at all (Codex).
/// </summary>
public sealed record AgentUsageReport(
    long InputTokens, long OutputTokens, long CacheReadTokens, long CacheCreationTokens,
    long ReasoningTokens, long Turns, double? CostUsd)
{
    public bool IsValid =>
        InputTokens >= 0 && OutputTokens >= 0 && CacheReadTokens >= 0 && CacheCreationTokens >= 0 &&
        ReasoningTokens >= 0 && Turns >= 0 &&
        (CostUsd is null || (double.IsFinite(CostUsd.Value) && CostUsd.Value >= 0));

    /// <summary>
    /// What this report adds to the previous one. Totals only grow while the CLI's record exists;
    /// any that went down means it started over (a fresh pod without the archived state), and the
    /// whole report is new usage — the same rule as the pod resource counters.
    /// </summary>
    public AgentUsageReport Since(AgentUsageReport previous)
    {
        var restarted = InputTokens < previous.InputTokens || OutputTokens < previous.OutputTokens ||
                        CacheReadTokens < previous.CacheReadTokens || CacheCreationTokens < previous.CacheCreationTokens ||
                        ReasoningTokens < previous.ReasoningTokens || Turns < previous.Turns ||
                        (CostUsd ?? 0) < (previous.CostUsd ?? 0);
        if (restarted) return this;
        return new AgentUsageReport(
            InputTokens - previous.InputTokens, OutputTokens - previous.OutputTokens,
            CacheReadTokens - previous.CacheReadTokens, CacheCreationTokens - previous.CacheCreationTokens,
            ReasoningTokens - previous.ReasoningTokens, Turns - previous.Turns,
            CostUsd is null ? null : CostUsd.Value - (previous.CostUsd ?? 0));
    }
}
