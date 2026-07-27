using AgentHub.Api.Otel;

namespace AgentHub.Api.Usage;

/// <summary>
/// Static Anthropic API price table used to estimate what a session's tokens WOULD have cost
/// on the API. Needed because Claude Code reports <c>claude_code.cost.usage = 0</c> when the
/// session runs on a Claude subscription (OAuth login) — tokens flow, cost does not.
///
/// Prices are USD per million tokens (input / output). Cache reads bill at 0.1x the input
/// rate, cache writes at 1.25x (5-minute TTL default). Matching is by substring on the model
/// id (e.g. "claude-opus-4-8"), most specific rule first; unknown models fall back to Sonnet
/// pricing as a middle-of-the-road estimate.
/// </summary>
public static class ClaudePricing
{
    private const double CacheReadFactor = 0.1;
    private const double CacheWriteFactor = 1.25;

    // (needle, input $/MTok, output $/MTok) — order matters: first match wins.
    private static readonly (string Needle, double Input, double Output)[] Rules =
    {
        ("fable", 10, 50),
        ("mythos", 10, 50),
        ("opus-4-1", 15, 75),
        ("opus-4-0", 15, 75),
        ("opus-4-2025", 15, 75),
        ("3-opus", 15, 75),
        ("opus", 5, 25),
        ("3-5-haiku", 0.8, 4),
        ("3-haiku", 0.25, 1.25),
        ("haiku", 1, 5),
        ("sonnet", 3, 15),
    };

    private static readonly (double Input, double Output) Fallback = (3, 15); // Sonnet rates

    /// <summary>Input/output USD-per-MTok rates for a model id ("" or unknown → fallback).</summary>
    public static (double Input, double Output) Rates(string? model)
    {
        if (!string.IsNullOrEmpty(model))
            foreach (var (needle, input, output) in Rules)
                if (model.Contains(needle, StringComparison.OrdinalIgnoreCase))
                    return (input, output);
        return Fallback;
    }

    /// <summary>Estimated API cost (USD) for one model's token counts.</summary>
    public static double EstimateUsd(string? model, long input, long output, long cacheRead, long cacheCreation)
    {
        var (inRate, outRate) = Rates(model);
        return (input * inRate
                + output * outRate
                + cacheRead * inRate * CacheReadFactor
                + cacheCreation * inRate * CacheWriteFactor) / 1_000_000d;
    }

    /// <summary>
    /// Estimated API cost (USD) for a whole usage delta, summed over its per-model buckets.
    /// Deltas from pods older than the per-model breakdown have an empty ModelTokens map; the
    /// aggregate counters are then priced at the fallback rate so estimates never drop to 0.
    /// </summary>
    public static double EstimateUsd(SessionUsageDelta delta)
    {
        if (delta.ModelTokens.Count == 0)
            return EstimateUsd(null, delta.InputTokens, delta.OutputTokens,
                delta.CacheReadTokens, delta.CacheCreationTokens);

        double sum = 0;
        foreach (var (model, t) in delta.ModelTokens)
            sum += EstimateUsd(model, t.InputTokens, t.OutputTokens, t.CacheReadTokens, t.CacheCreationTokens);
        return sum;
    }
}
