namespace AgentHub.Api.Models;

/// <summary>How a stored secret reaches a session (docs/session-secrets.md).</summary>
public static class SessionSecretDelivery
{
    /// <summary>An environment variable of the agent container, read when the container starts.</summary>
    public const string Env = "env";
    /// <summary>A file in <c>$HOME/.secrets</c>, kept current while the session runs.</summary>
    public const string File = "file";

    public static bool IsValid(string? value) => value is Env or File;
}

/// <summary>
/// What a listing shows of a stored secret: everything except the value, which is written and
/// never read back to any client.
/// </summary>
public sealed record SessionSecretInfo(string Id, string Name, string Delivery, string? Description, DateTime UpdatedAt);

/// <summary>
/// Stores a secret, or replaces the value of the one stored under the same name. A null
/// <see cref="Value"/> on an existing name keeps the stored value, so delivery and description
/// can be edited without the owner typing the secret again.
/// </summary>
public sealed record UpsertSessionSecretRequest(string? Name, string? Delivery, string? Value, string? Description);
