using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace AgentHub.Api.Controllers;

/// <summary>Read-only discovery through the token owner's existing Git OAuth connections.
/// Credential scopes cannot currently grant OAuth connections, so restricted tokens fail closed.</summary>
[ApiController]
[AllowAnonymous]
[Route("api/remote/git")]
public sealed class RemoteGitController : ControllerBase
{
    public const string OAuthNotAllowed = "git_oauth_not_allowed";
    private readonly Func<string, CancellationToken, Task<RemoteCaller?>> _findCaller;
    private readonly IGitAuthService _git;

    [ActivatorUtilitiesConstructor]
    public RemoteGitController(ApiTokenStore tokens, IGitAuthService git)
        : this(tokens.FindCallerByTokenAsync, git) { }

    public RemoteGitController(Func<string, CancellationToken, Task<RemoteCaller?>> findCaller, IGitAuthService git)
    {
        _findCaller = findCaller;
        _git = git;
    }

    private Task<RemoteCaller?> CallerAsync(CancellationToken ct)
        => RemoteBearerToken.Read(Request) is { } token
            ? _findCaller(token, ct) : Task.FromResult<RemoteCaller?>(null);

    [HttpGet("providers")]
    public async Task<ActionResult<IReadOnlyList<GitProviderInfo>>> Providers(CancellationToken ct)
    {
        var caller = await CallerAsync(ct);
        if (caller is null) return Unauthorized();
        if (caller.Scope is not null) return StatusCode(StatusCodes.Status403Forbidden, OAuthNotAllowed);
        return Ok(await _git.ListProvidersAsync(caller.Owner, ct));
    }

    [HttpGet("projects")]
    public async Task<ActionResult<IReadOnlyList<GitProject>>> Projects(
        [FromQuery] string provider, [FromQuery] string? q, CancellationToken ct)
    {
        var caller = await CallerAsync(ct);
        if (caller is null) return Unauthorized();
        if (caller.Scope is not null) return StatusCode(StatusCodes.Status403Forbidden, OAuthNotAllowed);
        if (string.IsNullOrWhiteSpace(provider) || provider.Length > 128 || (q?.Length ?? 0) > 256)
            return BadRequest("invalid_git_query");
        // Only configured provider IDs are accepted, never arbitrary upstream URLs.
        if (!_git.IsConfigured(provider)) return NotFound();
        try { return Ok(await _git.SearchProjectsAsync(caller.Owner, provider, q, ct)); }
        catch (ArgumentException) { return BadRequest("invalid_git_query"); }
        catch (InvalidOperationException) { return Conflict("git_provider_not_connected"); }
        catch (HttpRequestException) { return StatusCode(StatusCodes.Status502BadGateway, "git_provider_unavailable"); }
    }
}
