namespace AgentHub.Api.Services;

public sealed class RuntimeUpdateConfirmationRequiredException(string message) : InvalidOperationException(message);
