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

/// <summary>
/// What answering a secret request gave the session: the secret, under the name the session sees
/// it, and whether a restart is needed for it to arrive — a variable only reaches a container
/// that starts after it was granted.
/// </summary>
public sealed record SessionSecretGrant(string SecretId, string Name, string Delivery, bool RestartRequired);

/// <summary>An agent asks for a secret: a name, how it wants it, what it is for. Never a value.</summary>
public sealed record SecretRequestBody(string? Name, string? Delivery, string? Purpose);

/// <summary>The owner answers a request with a stored secret.</summary>
public sealed record ProvideSecretRequestBody(string? SecretId);

/// <summary>An owner grant is private to this session unless inheritance is explicitly enabled.</summary>
public sealed record SessionSecretAccess(string SecretId, bool InheritToChildren = false);

/// <summary>Persisted provenance is assigned by the service, never trusted from a request.</summary>
public sealed record StoredSessionSecretGrant(string SecretId, bool InheritToChildren, string Source, string? SourceSessionId = null);

public sealed record SessionSecretProvenance(string Id, string Name, string Delivery, string Provenance, bool InheritToChildren);
