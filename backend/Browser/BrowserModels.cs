namespace AgentHub.Api.Browser;

public enum BrowserPhase
{
    Stopped,
    Pending,
    Running,
    Stopping,
    Failed
}

public sealed record BrowserSummary(
    BrowserPhase Phase,
    int ScreenWidth = 1440,
    int ScreenHeight = 900,
    string? FailureCode = null)
{
    public static BrowserSummary Stopped { get; } = new(BrowserPhase.Stopped);
}

public sealed record BrowserConnection(
    BrowserSummary Browser,
    string CdpEndpoint,
    string PodIp);

public sealed record BrowserStateUrls(string GetUrl, string PutUrl);

public sealed record BrowserLease(
    string SessionId,
    string LeaseId,
    byte[] TokenHash,
    BrowserPhase Phase,
    string? PodIp,
    string? FailureCode,
    DateTime CreatedAt,
    DateTime UpdatedAt)
{
    public static BrowserLease Pending(string sessionId, string leaseId, byte[] tokenHash)
    {
        var now = DateTime.UtcNow;
        return new BrowserLease(sessionId, leaseId, tokenHash, BrowserPhase.Pending,
            null, null, now, now);
    }

    public BrowserSummary ToSummary(int width = 1440, int height = 900) =>
        new(Phase, width, height, FailureCode);
}
