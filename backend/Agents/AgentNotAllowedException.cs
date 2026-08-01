namespace AgentHub.Api.Agents;

/// <summary>
/// Thrown when a session lifecycle operation targets an agent kind that is not on
/// the instance allowlist. Controllers map this to HTTP 403.
/// </summary>
public sealed class AgentNotAllowedException(string message) : Exception(message);
