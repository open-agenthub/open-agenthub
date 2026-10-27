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
/// Stores a new secret, or, with <see cref="Id"/>, changes the stored one with that id: name,
/// delivery, description, and the value when <see cref="Value"/> is not null. A null value on an
/// update keeps the stored one, so the rest can be edited without the owner typing the secret
/// again. The id identifies a secret, not the name: two may share a name (#253).
/// </summary>
public sealed record UpsertSessionSecretRequest(string? Name, string? Delivery, string? Value, string? Description,
    string? Id = null);

/// <summary>
/// What answering a secret request gave the session: the secret, under the name the session sees
/// it, and whether a restart is needed for it to arrive — a variable only reaches a container
/// that starts after it was granted.
/// </summary>
public sealed record SessionSecretGrant(string SecretId, string Name, string Delivery, bool RestartRequired);

/// <summary>An agent asks for a secret: a name, how it wants it, what it is for. Never a value.</summary>
public sealed record SecretRequestBody(string? Name, string? Delivery, string? Purpose);

/// <summary>
/// The owner answers a request: with a stored secret by <see cref="SecretId"/>, or with
/// <see cref="Secret"/>, which is stored in the same call so that a refused grant leaves nothing
/// stored (#330).
/// </summary>
public sealed record ProvideSecretRequestBody(string? SecretId, UpsertSessionSecretRequest? Secret = null);

/// <summary>An owner grant is private to this session unless inheritance is explicitly enabled.</summary>
public sealed record SessionSecretAccess(string SecretId, bool InheritToChildren = false);

/// <summary>Persisted provenance is assigned by the service, never trusted from a request.</summary>
public sealed record StoredSessionSecretGrant(string SecretId, bool InheritToChildren, string Source, string? SourceSessionId = null);

public sealed record SessionSecretProvenance(string Id, string Name, string Delivery, string Provenance, bool InheritToChildren);
