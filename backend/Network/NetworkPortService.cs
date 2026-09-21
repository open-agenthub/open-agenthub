using AgentHub.Api.Permissions;
using AgentHub.Api.Persistence;

namespace AgentHub.Api.Network;

/// <summary>Session-scoped cleanup hook, called when a session is deleted.</summary>
public interface INetworkSessionCleanup
{
    Task CleanupSessionAsync(string sessionId, CancellationToken ct = default);
}

/// <summary>What the agent asked for, already syntax-validated by the controller.</summary>
public sealed record PortRequestInput(PortDirection Direction, int Port, string Protocol, string? Reason);

/// <summary>
/// Either an immediate decision ("allow" when already granted/auto-approved, "deny"
/// when outside the allowlist) or a pending request id the agent polls.
/// </summary>
public sealed record PortRequestOutcome(string? RequestId, string? Decision, string? Reason)
{
    public static PortRequestOutcome Pending(string id) => new(id, null, null);
    public static PortRequestOutcome Decided(string decision, string? reason = null) => new(null, decision, reason);
}

/// <summary>
/// Runtime port requests: allowlist enforcement, user approval via the existing
/// tool-permission channel (messenger prompts + in-app approval both work unchanged),
/// and NetworkPolicy creation once approved. Policies are applied lazily on the
/// agent's decision poll, so approval surfaces never need to know about Kubernetes.
/// </summary>
public sealed class NetworkPortService : INetworkSessionCleanup
{
    private readonly NetworkPortOptions _options;
    private readonly NetworkPortAllowlist _allowlist;
    private readonly string _namespace;
    private readonly PermissionStore _permissions;
    private readonly IPortGrantStore _grants;
    private readonly INetworkPolicyClient _policies;
    private readonly IEnumerable<IPermissionNotifier> _notifiers;
    private readonly IEnumerable<IPermissionPromptEditor> _promptEditors;
    private readonly ILogger<NetworkPortService> _log;

    private const int MaxReasonLength = 300;

    public NetworkPortService(IConfiguration cfg, PermissionStore permissions, IPortGrantStore grants,
        INetworkPolicyClient policies, IEnumerable<IPermissionNotifier> notifiers,
        IEnumerable<IPermissionPromptEditor> promptEditors, ILogger<NetworkPortService> log)
    {
        _options = cfg.GetSection("Network").Get<NetworkPortOptions>() ?? new NetworkPortOptions();
        _allowlist = NetworkPortAllowlist.Parse(_options.RequestablePorts);
        _namespace = cfg["AgentHub:Namespace"] ?? "agenthub-sessions";
        _permissions = permissions;
        _grants = grants;
        _policies = policies;
        _notifiers = notifiers;
        _promptEditors = promptEditors;
        _log = log;
    }

    public bool Enabled => _options.Enabled;

    public async Task<PortRequestOutcome> RequestAsync(SessionRecord session, PortRequestInput input, CancellationToken ct = default)
    {
        if (!_options.Enabled)
            return PortRequestOutcome.Decided("deny", "Network port requests are disabled on this instance.");
        // The allowlist is checked BEFORE anyone is asked: a port outside it can never
        // be opened, no matter what the user would click.
        if (!_allowlist.IsAllowed(input.Port))
            return PortRequestOutcome.Decided("deny",
                $"Port {input.Port} is not on this instance's requestable-ports allowlist.");

        var tool = PortRequestKey.Format(input.Direction, input.Port, input.Protocol);
        if (await _grants.ExistsAsync(session.Id, input.Direction, input.Port, input.Protocol, ct))
            return PortRequestOutcome.Decided("allow", "Already granted for this session.");
        if (session.AutoApprove || await _permissions.IsAlwaysAllowedAsync(session.Id, tool, ct))
        {
            await EnsureGrantedAsync(session.Id, input.Direction, input.Port, input.Protocol, ct);
            return PortRequestOutcome.Decided("allow");
        }

        var request = new PermissionRequest
        {
            Id = Guid.NewGuid().ToString("n")[..12],
            SessionId = session.Id,
            Owner = session.Owner,
            Tool = tool,
            Summary = Truncate(input.Reason)
        };
        await _permissions.CreateAsync(request, ct);
        await PermissionRelay.TryPostAsync(_notifiers, request, ct);
        return PortRequestOutcome.Pending(request.Id);
    }

    /// <summary>
    /// Polled by the agent. Applies the NetworkPolicies on the first poll that sees an
    /// approval — idempotent, so replica races and re-polls are harmless.
    /// </summary>
    public async Task<string> GetDecisionAsync(SessionRecord session, string requestId, CancellationToken ct = default)
    {
        var request = await _permissions.GetAsync(requestId, session.Id, ct);
        if (request is null) return "expired";
        if (request.Decision is null) return "pending";
        if (request.Decision is not ("allow" or "allowAlways")) return request.Decision;
        if (!PortRequestKey.TryParse(request.Tool, out var direction, out var port, out var protocol))
            return "deny"; // Not a port request id — refuse to open anything for it.
        await EnsureGrantedAsync(session.Id, direction, port, protocol, ct);
        return "allow";
    }

    /// <summary>The agent gave up waiting; mirrors the tool-permission expire semantics.</summary>
    public async Task<string> ExpireAsync(SessionRecord session, string requestId, CancellationToken ct = default)
    {
        var resolved = await _permissions.ResolveAsync(requestId, "expired", session.Id, ct);
        if (resolved is null)
        {
            // A decision won the race against the expiry — honor it (incl. policy creation).
            return await GetDecisionAsync(session, requestId, ct);
        }
        if (resolved.Platform is { } platform)
            foreach (var editor in _promptEditors.Where(e => e.Platform == platform))
                await editor.MarkExpiredAsync(resolved, ct);
        return "expired";
    }

    public Task<IReadOnlyList<PortGrant>> ListGrantsAsync(string sessionId, CancellationToken ct = default) =>
        _grants.ListAsync(sessionId, ct);

    public async Task CleanupSessionAsync(string sessionId, CancellationToken ct = default)
    {
        await _policies.DeleteBySessionAsync(_namespace, sessionId, ct);
        await _grants.DeleteBySessionAsync(sessionId, ct);
    }

    private async Task EnsureGrantedAsync(string sessionId, PortDirection direction, int port, string protocol, CancellationToken ct)
    {
        var policies = NetworkPolicyFactory.Build(sessionId, _namespace, direction, port, protocol);
        await _policies.CreateAsync(policies, ct);
        await _grants.AddAsync(sessionId, direction, port, protocol, ct);
        _log.LogInformation("Opened {Direction} port {Port}/{Protocol} for session {SessionId}",
            PortRequestKey.WireName(direction), port, protocol, sessionId);
    }

    private static string? Truncate(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return null;
        var trimmed = reason.Trim();
        return trimmed.Length <= MaxReasonLength ? trimmed : trimmed[..MaxReasonLength];
    }
}
