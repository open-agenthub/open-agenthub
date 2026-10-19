using System.Globalization;
using k8s.Models;

namespace AgentHub.Api.Services;

/// <summary>
/// The CPU and memory a session pod requests and is limited to (#162, #215,
/// docs/session-resources.md). A request is what the scheduler reserves on a node; the limit is
/// what the container may use before it is throttled (CPU) or killed (memory). A request below a
/// useful floor reserves nothing — <c>10m</c> memory is a hundredth of a byte, usually CPU
/// notation typed into the memory field — so the scheduler overcommits the node and the pod is
/// among the first evicted under memory pressure, taking its workspace with it. A request above
/// the pod's own limit is rejected by Kubernetes when the pod is created, so it is refused here,
/// when the session is stored, instead.
/// </summary>
public static class SessionResources
{
    /// <summary>Throws <see cref="ArgumentException"/> with a message a caller can act on when
    /// <paramref name="value"/> is not a quantity, or outside <paramref name="min"/>..<paramref name="max"/>.
    /// <paramref name="sessionLimit"/> says the maximum is the session's own limit rather than
    /// the instance's, which is what the caller has to change to raise it.</summary>
    public static void Validate(string? value, string what, string min, string max, bool sessionLimit = false)
    {
        var amount = Parse(value) ?? throw new ArgumentException($"Invalid {what} quantity: '{value}'.");
        if (amount < Parse(min))
            throw new ArgumentException($"{Capitalized(what)} request '{value}' is below the minimum of {min}.{MilliHint(value!, what)}");
        if (amount > Parse(max))
            throw new ArgumentException(sessionLimit
                ? $"{Capitalized(what)} request '{value}' is above this session's {what} limit of {max}."
                : $"{Capitalized(what)} request '{value}' is above the maximum of {max} on this instance.");
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

    /// <summary>A limit as stored: null for "the instance default", which an empty string asks for.</summary>
    public static string? NormalizeLimit(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// Checks a new session's request and optional limit of one resource and returns the limit to
    /// store (null = the instance default). The request must lie between the floor and the limit
    /// the pod will get, so a pod Kubernetes would refuse is never stored.
    /// </summary>
    public static string? ForCreate(string? request, string? limit, ResourceBounds bounds)
    {
        var stored = NormalizeLimit(limit);
        if (stored is not null) ValidateLimit(stored, bounds);
        Validate(request, bounds.What, bounds.Min, stored ?? bounds.DefaultLimit, sessionLimit: stored is not null);
        return stored;
    }

    /// <summary>
    /// The request and limit a session holds after an edit. As in #162 only a changed value is
    /// checked against the bounds, so a session stored before them stays editable when the edit
    /// dialog sends its values back unchanged. A lowered limit is still held against the unchanged
    /// request: together they become the pod spec, and a request above its limit is refused.
    /// </summary>
    public static (string Request, string? Limit) ForUpdate(string storedRequest, string? storedLimit,
        string? request, string? limit, ResourceBounds bounds)
    {
        var limitChanged = limit is not null && NormalizeLimit(limit) != storedLimit;
        var newLimit = limitChanged ? NormalizeLimit(limit) : storedLimit;
        if (limitChanged && newLimit is not null) ValidateLimit(newLimit, bounds);
        var effectiveLimit = newLimit ?? bounds.DefaultLimit;

        var requestChanged = request is not null && request != storedRequest;
        if (requestChanged)
        {
            Validate(request, bounds.What, bounds.Min, effectiveLimit, sessionLimit: newLimit is not null);
            return (request!, newLimit);
        }
        if (limitChanged && Parse(storedRequest) is { } amount && amount > Parse(effectiveLimit))
            throw new ArgumentException(
                $"{Capitalized(bounds.What)} limit {effectiveLimit} is below this session's {bounds.What} request of {storedRequest.Trim()}; lower the request as well.");
        return (storedRequest, newLimit);
    }

    /// <summary>
    /// The request and limit a pod is started with. A stored limit the operator has since moved
    /// the ceiling below is clamped like a stored request is, and the request then follows the
    /// limit down: the record keeps what the owner set, the pod gets a spec Kubernetes accepts.
    /// </summary>
    public static (string Request, string Limit) EffectivePair(string? request, string? limit, ResourceBounds bounds)
    {
        var effectiveLimit = NormalizeLimit(limit) is { } stored && Parse(stored) is not null
            ? Effective(stored, bounds.Min, bounds.Ceiling)
            : bounds.DefaultLimit;
        return (Effective(request, bounds.Min, effectiveLimit), effectiveLimit);
    }

    /// <summary>Whether two quantities are the same amount ("1" and "1000m" are).</summary>
    public static bool SameAmount(string? a, string? b) => Parse(a) is { } x && Parse(b) is { } y && x == y;

    private static void ValidateLimit(string value, ResourceBounds bounds)
    {
        var what = bounds.What;
        var amount = Parse(value) ?? throw new ArgumentException($"Invalid {what} limit quantity: '{value}'.");
        if (amount < Parse(bounds.Min))
            throw new ArgumentException($"{Capitalized(what)} limit '{value}' is below the minimum of {bounds.Min}.{MilliHint(value, what)}");
        if (amount > Parse(bounds.Ceiling))
            throw new ArgumentException($"{Capitalized(what)} limit '{value}' is above the ceiling of {bounds.Ceiling} on this instance.");
    }

    // The mistake behind every case seen: CPU notation (m = milli) in the memory field.
    private static string MilliHint(string value, string what) =>
        what == "memory" && value.Trim().EndsWith('m')
            ? $" The suffix m means milli, so '{value}' is a fraction of a byte; memory is given in Mi or Gi (e.g. '512Mi', '1Gi')."
            : "";

    internal static decimal? Parse(string? value)
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

/// <summary>
/// The bounds of one resource. <see cref="DefaultLimit"/> is the limit of a session that sets
/// none, <c>agent.maxCpu</c>/<c>agent.maxMemory</c> as before #215; <see cref="Ceiling"/> is the
/// most a session may set for itself. A ceiling below the default wins over it: it is the bound an
/// operator set to keep sessions under, and a default above it would break that for every session.
/// </summary>
public sealed record ResourceBounds(string What, string Min, string DefaultLimit, string Ceiling)
{
    public static ResourceBounds Cpu(AgentHubOptions o) => Of("cpu", o.MinCpu, o.MaxCpu, o.CpuLimitCeiling);
    public static ResourceBounds Memory(AgentHubOptions o) => Of("memory", o.MinMemory, o.MaxMemory, o.MemoryLimitCeiling);

    private static ResourceBounds Of(string what, string min, string max, string ceiling)
    {
        var top = string.IsNullOrWhiteSpace(ceiling) ? max : ceiling.Trim();
        var defaultLimit = SessionResources.Parse(max) is { } m && SessionResources.Parse(top) is { } c && c < m ? top : max;
        return new ResourceBounds(what, min, defaultLimit, top);
    }
}

/// <summary>What a session's pod requests and is limited to (shown in the session details).</summary>
public sealed record EffectiveSessionResources(string CpuRequest, string CpuLimit, string MemoryRequest, string MemoryLimit);
