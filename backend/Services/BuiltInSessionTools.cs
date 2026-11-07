namespace AgentHub.Api.Services;

/// <summary>
/// The hub's own MCP tools that only read the session's state or clean up what the session itself
/// started (#504, docs/builtin-session-tools-policy.md). A session's allow list does not have to
/// name them: an unattended session with auto-approve off and an allow list naming container_run
/// could start a helper container and was then refused container_stop for it, and could not read
/// its own inbox. The owner's MCP sharing block still applies to them, and is checked before this.
///
/// Tools that extend what the session can do stay under the allow list: session_create (a child
/// can be started with auto-approve on, which would be a way around this session's own list),
/// container_run, secret_request, port_request, agent_send, session_delete, session_convert,
/// account_switch, context_clear and the browser tools.
/// </summary>
public static class BuiltInSessionTools
{
    private static readonly HashSet<string> SelfService = new(StringComparer.Ordinal)
    {
        "mcp__agenthub_sessions__agent_inbox",
        "mcp__agenthub_sessions__agents_list",
        "mcp__agenthub_sessions__session_list",
        "mcp__agenthub_sessions__session_get",
        "mcp__agenthub_sessions__session_wait",
        "mcp__agenthub_sessions__container_list",
        "mcp__agenthub_sessions__container_logs",
        "mcp__agenthub_sessions__container_stop",
        "mcp__agenthub_sessions__secrets_list",
        "mcp__agenthub_sessions__account_status",
        "mcp__agenthub_network__port_list"
    };

    /// <summary>Whether <paramref name="tool"/> (as the runtimes report it, mcp__server__tool) is one
    /// of the self-service built-ins.</summary>
    public static bool IsSelfService(string? tool) => tool is not null && SelfService.Contains(tool);
}
