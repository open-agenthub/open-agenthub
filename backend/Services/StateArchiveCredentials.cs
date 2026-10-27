using AgentHub.Api.Models;

namespace AgentHub.Api.Services;

public static class StateArchiveCredentials
{
    public static bool Contains(AgentKind agent, string name)
    {
        if (IsCanonical(agent, name)) return true;
        var slash = name.LastIndexOf('/');
        var file = name[(slash + 1)..];
        if (!file.StartsWith('.') || !file.EndsWith(".tmp", StringComparison.Ordinal)) return false;
        var stem = file[..^4];
        var separator = stem.LastIndexOf('.');
        if (separator <= 1) return false;
        var pid = stem[(separator + 1)..];
        if (pid.Length == 0 || pid[0] == '0' || !pid.All(char.IsAsciiDigit)) return false;
        // Match the writer's sibling name only at known credential locations, preserving history.
        return IsCanonical(agent, name[..(slash + 1)] + stem[1..separator]);
    }

    private static bool IsCanonical(AgentKind agent, string name)
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
