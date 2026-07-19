namespace AgentHub.Api.Permissions;

/// <summary>Encoding/decoding of the permission button action_id ("perm:&lt;decision&gt;:&lt;id&gt;").</summary>
public static class PermissionAction
{
    public static string Id(string decision, string reqId) => $"perm:{decision}:{reqId}";

    public static bool TryParse(string? actionId, out string decision, out string reqId)
    {
        decision = ""; reqId = "";
        if (string.IsNullOrEmpty(actionId)) return false;
        var p = actionId.Split(':');
        if (p.Length != 3 || p[0] != "perm" || p[1].Length == 0 || p[2].Length == 0) return false;
        decision = p[1]; reqId = p[2];
        return true;
    }
}
