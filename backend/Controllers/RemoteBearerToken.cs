namespace AgentHub.Api.Controllers;

/// <summary>
/// Reads the personal API token out of a request on the <c>api/remote</c> surface. Shared by
/// every controller there, because the <c>oah_</c> prefix check is the one thing that stops a
/// share-link token or an OAuth bearer from being hashed and looked up as a personal token — a
/// copy of these lines that lost it in one controller would not be noticed by the other.
/// </summary>
public static class RemoteBearerToken
{
    private const string Scheme = "Bearer ";
    private const string Prefix = "oah_";

    /// <summary>The token, or null when the header is missing, not Bearer, or not a personal token.</summary>
    public static string? Read(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        if (!header.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase)) return null;

        var token = header[Scheme.Length..].Trim();
        return token.StartsWith(Prefix, StringComparison.Ordinal) ? token : null;
    }
}
