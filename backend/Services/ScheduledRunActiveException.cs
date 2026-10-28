namespace AgentHub.Api.Services;

/// <summary>A scheduled session cannot change project while one of its runs is still active; the
/// caller may retry once it finishes or ask for the run to be stopped.</summary>
public sealed class ScheduledRunActiveException(string message) : InvalidOperationException(message);
