namespace AgentHub.Api.Permissions;

public static class PermissionRequestDescriptor
{
    public static string ForTool(string? tool)
    {
        if (string.Equals(tool, "Bash", StringComparison.Ordinal)) return "Bash command";
        if (tool?.StartsWith("mcp__", StringComparison.Ordinal) == true) return "MCP tool request";
        if (string.Equals(tool, "apply_patch", StringComparison.Ordinal)
            || string.Equals(tool, "Edit", StringComparison.Ordinal)
            || string.Equals(tool, "Write", StringComparison.Ordinal))
            return "File change request";
        return "Tool request";
    }
}
