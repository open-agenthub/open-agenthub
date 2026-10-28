namespace AgentHub.Api.Services;

/// <summary>
/// Some sessions' secret copies could be neither rewritten nor withdrawn, so they may still hold a
/// value the owner removed (#303). The store change has happened; repeating the request repeats
/// the sync. Reported instead of a plain success, which must mean no copy still holds the value.
/// </summary>
public sealed class SessionSecretCopiesPendingException(int sessions)
    : Exception($"{sessions} session(s) may still hold the previous secret values: their copies could not be updated or removed. Retry to finish.")
{
    public int Sessions { get; } = sessions;
}
