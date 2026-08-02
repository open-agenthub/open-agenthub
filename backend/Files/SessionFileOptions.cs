namespace AgentHub.Api.Files;

public sealed class SessionFileOptions
{
    public long MaxImageBytes { get; init; } = 20L * 1024 * 1024;
    public long MaxDocumentBytes { get; init; } = 50L * 1024 * 1024;
    public long MaxMessageBytes { get; init; } = 50L * 1024 * 1024;
    public int MaxMessageFiles { get; init; } = 5;
    public int MaxSessionFiles { get; init; } = 200;
    public long MaxSessionBytes { get; init; } = 1024L * 1024 * 1024;
    public int PresignMinutes { get; init; } = 10;
    public int ReservationMinutes { get; init; } = 15;
    public int PresentationPollMilliseconds { get; init; } = 1500;
    [System.Text.Json.Serialization.JsonIgnore] public OfficePreviewOptions OfficePreview { get; init; } = new();
}

public sealed class OfficePreviewOptions
{
    public bool Enabled { get; init; }
    public string BaseUrl { get; init; } = "http://agenthub-artifact-renderer:8080/";
    public int TimeoutSeconds { get; init; } = 90;
    public long MaxOutputBytes { get; init; } = 50L * 1024 * 1024;
    public int PollMilliseconds { get; init; } = 1000;
}
