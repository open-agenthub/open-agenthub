using AgentHub.Api.Models;

namespace AgentHub.Api.Services;

public static class StateArchiveCredentials
{
    public static bool Contains(AgentKind agent, string name)
    {
        return agent switch
        {
            AgentKind.Claude => name == ".claude/.credentials.json",
            AgentKind.Codex => name == ".codex/auth.json",
            AgentKind.Cursor => name == ".cursor/auth.json",
            AgentKind.OpenCode => name is ".opencode/auth.json" or ".opencode/mcp-auth.json",
            AgentKind.OpenClaw => name == ".openclaw/auth-profiles.json" || IsNestedOpenClawStore(name),
            _ => false
        };
    }

    private static bool IsNestedOpenClawStore(string name)
    {
        var parts = name.Split('/');
        // Restrict removal to the credential-store slot, preserving similarly named session files.
        return parts.Length == 5 && parts[0] == ".openclaw" && parts[1] == "agents" &&
            parts[2].Length > 0 && parts[3] == "agent" && parts[4] is
                "auth-profiles.json" or "openclaw-agent.sqlite" or "openclaw-agent.sqlite-wal" or "openclaw-agent.sqlite-shm";
    }
}
