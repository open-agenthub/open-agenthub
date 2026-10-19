using System.Globalization;
using k8s.Models;

namespace AgentHub.Api.Services;

/// <summary>
/// The CPU and memory a session pod requests (#162, docs/session-resources.md). A request is what
/// the scheduler reserves on a node; the limit stays at the instance maximum. A request below a
/// useful floor reserves nothing — <c>10m</c> memory is a hundredth of a byte, usually CPU
/// notation typed into the memory field — so the scheduler overcommits the node and the pod is
/// among the first evicted under memory pressure, taking its workspace with it. A request above
/// the maximum exceeds the pod's own limit, which Kubernetes rejects when the pod is created.
/// </summary>
public static class SessionResources
{
    /// <summary>Throws <see cref="ArgumentException"/> with a message a caller can act on when
    /// <paramref name="value"/> is not a quantity, or outside <paramref name="min"/>..<paramref name="max"/>.</summary>
    public static void Validate(string? value, string what, string min, string max)
    {
        var amount = Parse(value) ?? throw new ArgumentException($"Invalid {what} quantity: '{value}'.");
        if (amount < Parse(min))
        {
            // The mistake behind every case seen: CPU notation (m = milli) in the memory field.
            var hint = what == "memory" && value!.Trim().EndsWith('m')
                ? $" The suffix m means milli, so '{value}' is a fraction of a byte; memory is given in Mi or Gi (e.g. '512Mi', '1Gi')."
                : "";
            throw new ArgumentException($"{Capitalized(what)} request '{value}' is below the minimum of {min}.{hint}");
        }
        if (amount > Parse(max))
            throw new ArgumentException($"{Capitalized(what)} request '{value}' is above the maximum of {max} on this instance.");
    }

    /// <summary>The value a pod is started with: <paramref name="value"/> inside the bounds, or the
    /// nearest bound. For sessions stored before the bounds existed, which may hold either kind.</summary>
    public static string Effective(string? value, string min, string max)
    {
        var amount = Parse(value);
        if (amount is null || amount < Parse(min)) return min;
        if (amount > Parse(max)) return max;
        return value!.Trim();
    }

    private static decimal? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try { return new ResourceQuantity(value.Trim()).ToDecimal(); }
        // What ResourceQuantity throws for text that is no quantity ("lots") or one too large to hold.
        catch (FormatException) { return null; }
        catch (OverflowException) { return null; }
    }

    private static string Capitalized(string what) =>
        what.Length == 0 ? what : char.ToUpper(what[0], CultureInfo.InvariantCulture) + what[1..];
}
