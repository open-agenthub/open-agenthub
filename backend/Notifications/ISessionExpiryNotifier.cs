using AgentHub.Api.Persistence;

namespace AgentHub.Api.Notifications;

/// <summary>Captures a destination before deletion removes its mapping. The returned notifier
/// is invoked only after deletion succeeds, so it cannot announce a failed delete.</summary>
public interface ISessionExpiryNotifier
{
    Task<INotifier> PrepareExpiryAsync(SessionRecord session, CancellationToken ct = default);
}
