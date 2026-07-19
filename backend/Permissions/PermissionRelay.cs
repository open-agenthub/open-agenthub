namespace AgentHub.Api.Permissions;

/// <summary>Fans a permission request out over the registered notifiers (Slack, Telegram, …).</summary>
public static class PermissionRelay
{
    /// <summary>First notifier that posts wins (registration order = priority).</summary>
    public static async Task<bool> TryPostAsync(IEnumerable<IPermissionNotifier> notifiers, PermissionRequest req, CancellationToken ct)
    {
        foreach (var n in notifiers)
            if (await n.PostAsync(req, ct)) return true;
        return false;
    }
}
