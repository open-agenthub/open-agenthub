using System.Security.Claims;
using System.Text.Json;
using AgentHub.Api.Ee.Library;
using AgentHub.Api.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentHub.Api.Controllers;

/// <summary>
/// Personal MCP server catalog. Users manage their own entries; shared/org
/// entries appear in the list without ConfigJson. Secrets are never returned.
/// </summary>
[ApiController]
[Authorize]
[Route("api/mcp-servers")]
public sealed class McpServersController : ControllerBase
{
    private readonly IMcpServerStore _store;
    private readonly ILibraryAccess _access;
    private readonly ILibraryShareStore _shares;

    public McpServersController(IMcpServerStore store, ILibraryAccess access, ILibraryShareStore shares)
    {
        _store = store;
        _access = access;
        _shares = shares;
    }

    private string Owner =>
        User.FindFirstValue("preferred_username")
        ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new UnauthorizedAccessException();

    [HttpGet]
    public async Task<IReadOnlyList<McpServerInfo>> List(CancellationToken ct)
    {
        var owner = Owner;
        var records = await _access.ListMcpServersAsync(owner, ct);
        return records.Select(r => ToInfo(r, mine: r.Owner == owner)).ToList();
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SaveMcpServerRequest request, CancellationToken ct)
    {
        try
        {
            return Ok(ToInfo(await _store.CreateAsync(Owner, request, ct), mine: true));
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { error = exception.Message });
        }
    }

    /// <summary>
    /// Create a <c>kind=api</c> catalog entry from an OpenAPI/GraphQL URL.
    /// Spec fetch validation is shallow until the gateway task (URL + config shape).
    /// </summary>
    [HttpPost("from-api")]
    public async Task<IActionResult> CreateFromApi(
        [FromBody] CreateMcpFromApiRequest request, CancellationToken ct)
    {
        if (!request.Save)
            return BadRequest(new { error = "save must be true for catalog create; use session ephemeral sources otherwise." });

        try
        {
            var configJson = BuildApiConfigJson(request);
            string? secretJson = null;
            if (!string.IsNullOrWhiteSpace(request.Secret))
            {
                var secret = request.Secret.Trim();
                secretJson = secret.StartsWith('{')
                    ? secret
                    : JsonSerializer.Serialize(new Dictionary<string, string> { ["token"] = secret });
            }

            var saved = await _store.CreateAsync(
                Owner,
                new SaveMcpServerRequest(
                    request.Name, request.Description, "api", configJson, secretJson),
                ct);
            return Ok(ToInfo(saved, mine: true));
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { error = exception.Message });
        }
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(
        string id, [FromBody] SaveMcpServerRequest request, CancellationToken ct)
    {
        try
        {
            return Ok(ToInfo(await _store.UpdateAsync(Owner, id, request, ct), mine: true));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { error = exception.Message });
        }
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        try
        {
            await _store.DeleteAsync(Owner, id, ct);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        // Share rows are metadata of the deleted item — clean up regardless of license.
        await _shares.DeleteForItemAsync(LibraryItemTypes.Mcp, id, ct);
        return NoContent();
    }

    internal static string BuildApiConfigJson(CreateMcpFromApiRequest request)
    {
        var specUrl = request.SpecUrl?.Trim() ?? "";
        if (string.IsNullOrEmpty(specUrl)
            || !Uri.TryCreate(specUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("specUrl must be an absolute http(s) URL.");
        }

        var specType = string.IsNullOrWhiteSpace(request.SpecType)
            ? "auto"
            : request.SpecType.Trim().ToLowerInvariant();
        if (specType is not ("openapi" or "graphql" or "auto"))
            throw new ArgumentException("specType must be 'openapi', 'graphql', or 'auto'.");

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("specType", specType);
            writer.WriteString("specUrl", specUrl);
            if (!string.IsNullOrWhiteSpace(request.BaseUrl))
                writer.WriteString("baseUrl", request.BaseUrl.Trim());
            if (request.Auth is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null } auth)
            {
                writer.WritePropertyName("auth");
                auth.WriteTo(writer);
            }
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    internal static McpServerInfo ToInfo(McpServerRecord record, bool mine) => new(
        record.Id,
        record.Name,
        record.Description,
        record.Owner,
        record.Kind,
        mine,
        mine ? record.ConfigJson : null,
        HasSecret: !string.IsNullOrEmpty(record.SecretJson),
        record.CreatedAt,
        record.UpdatedAt);
}

/// <summary>Body for <c>POST /api/mcp-servers/from-api</c>.</summary>
public sealed record CreateMcpFromApiRequest(
    string Name,
    string? Description,
    string SpecUrl,
    string? SpecType = null,
    string? BaseUrl = null,
    JsonElement? Auth = null,
    string? Secret = null,
    bool Save = true);
