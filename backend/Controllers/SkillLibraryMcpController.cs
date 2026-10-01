using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using AgentHub.Api.Library;
using AgentHub.Api.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentHub.Api.Controllers;

/// <summary>
/// Streamable-HTTP endpoint of the skill-library MCP server. Agent pods reach
/// it with their session callback token; the entry is injected into every
/// session's .mcp.json (see KubernetesSessionService). Requests are plain
/// JSON-RPC POSTs answered with JSON — no SSE stream is offered.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("internal/sessions/{id}/mcp")]
public sealed class SkillLibraryMcpController : ControllerBase
{
    // An upload may carry a SKILL.md of 200k characters plus 500k characters of helper files
    // (LibraryValidation's ceilings). As UTF-8 with JSON escaping that passes a megabyte well
    // before the library itself would refuse it, and the agent would see a 413 for a skill the
    // store would have accepted — most likely for the large script it was finally persuaded to
    // upload from a path.
    private const int MaxBodyBytes = 4_000_000;

    private readonly ISessionStore _store;
    private readonly SkillLibraryMcpService _mcp;
    private readonly SkillImporter _importer;
    private readonly ISkillStore _skills;

    public SkillLibraryMcpController(
        ISessionStore store, SkillLibraryMcpService mcp, SkillImporter importer, ISkillStore skills)
    {
        _store = store;
        _mcp = mcp;
        _importer = importer;
        _skills = skills;
    }

    [HttpPost]
    public async Task<IActionResult> Post(string id, CancellationToken ct)
    {
        if (!Request.Headers.TryGetValue("X-Agent-Token", out var token))
            return Unauthorized();
        var session = await _store.GetByCallbackTokenAsync(token!, ct);
        if (session is null || session.Id != id)
            return Unauthorized();

        if (Request.ContentLength is > MaxBodyBytes)
            return StatusCode(StatusCodes.Status413PayloadTooLarge);

        JsonDocument message;
        try
        {
            message = await JsonDocument.ParseAsync(Request.Body, cancellationToken: ct);
        }
        catch (JsonException)
        {
            return BadRequest(new { error = "Invalid JSON." });
        }

        using (message)
        {
            var response = await _mcp.HandleAsync(message.RootElement, session, ct);
            if (response is null)
                return Accepted();
            return Content(response.ToJsonString(), "application/json");
        }
    }

    /// <summary>No server-initiated stream: clients must use plain POSTs.</summary>
    [HttpGet]
    public IActionResult Get()
    {
        Response.Headers.Allow = "POST";
        return StatusCode(StatusCodes.Status405MethodNotAllowed);
    }

    /// <summary>
    /// Serves one stored file of a skill so the agent can put it on disk without its
    /// content passing through the model's context (<c>curl -o</c>), and so the local
    /// skills proxy can implement <c>get_skill(out_dir)</c>.
    ///
    /// The content comes from <see cref="ISkillStore"/>, which resolves object storage
    /// or the fallback column — the route therefore works on an instance without S3 too.
    /// It is a plain hub url authenticated by the session's callback token, not a
    /// presigned storage url: a presigned one expires while the agent is still working
    /// with it, which is the failure InternalSessionFilesController documents.
    /// </summary>
    [HttpGet("/internal/sessions/{id}/skills/{nameOrId}/files/{**path}")]
    public async Task<IActionResult> SkillFile(
        string id, string nameOrId, string path, [FromQuery] int? version, CancellationToken ct)
    {
        var (resolved, failure) = await ResolveSkillAsync(id, nameOrId, version, ct);
        if (resolved is null) return failure!;
        var (_, _, files, content) = resolved;

        if (string.Equals(path, "SKILL.md", StringComparison.Ordinal))
        {
            return content is null
                ? NotFound(new { error = "no_content" })
                : File(Encoding.UTF8.GetBytes(content), "text/markdown", "SKILL.md");
        }
        var file = files.FirstOrDefault(f => f.Path == path);
        if (file is null)
            return NotFound(new { error = "file_not_found", available = files.Select(f => f.Path).ToArray() });
        return File(Encoding.UTF8.GetBytes(file.Content), "application/octet-stream",
            file.Path.Split('/')[^1]);
    }

    /// <summary>
    /// The whole skill as a tar.gz — SKILL.md plus every extra file, laid out exactly as a
    /// skill directory. One request puts a complete skill on disk; the local skills proxy
    /// uses it for <c>get_skill(out_dir)</c>.
    /// </summary>
    [HttpGet("/internal/sessions/{id}/skills/{nameOrId}/files.tar.gz")]
    public async Task<IActionResult> SkillBundle(
        string id, string nameOrId, [FromQuery] int? version, CancellationToken ct)
    {
        var (resolved, failure) = await ResolveSkillAsync(id, nameOrId, version, ct);
        if (resolved is null) return failure!;
        var (record, effectiveVersion, files, content) = resolved;
        if (content is null) return NotFound(new { error = "no_content" });

        var entries = new List<SkillFile> { new("SKILL.md", content) };
        entries.AddRange(files);

        var buffer = new MemoryStream();
        await using (var gzip = new GZipStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
        await using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var entry in entries)
            {
                var bytes = Encoding.UTF8.GetBytes(entry.Content);
                // A shell helper that arrives without the execute bit is the first thing
                // that fails after the download, and the agent cannot tell why from the
                // error. Nothing else needs it.
                var mode = entry.Path.EndsWith(".sh", StringComparison.OrdinalIgnoreCase)
                    ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                      | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                      | UnixFileMode.OtherRead | UnixFileMode.OtherExecute
                    : UnixFileMode.UserRead | UnixFileMode.UserWrite
                      | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
                await tar.WriteEntryAsync(
                    new PaxTarEntry(TarEntryType.RegularFile, entry.Path)
                    {
                        DataStream = new MemoryStream(bytes),
                        Mode = mode
                    }, ct);
            }
        }
        return File(buffer.ToArray(), "application/gzip", $"{record.Name}-v{effectiveVersion}.tar.gz");
    }

    private sealed record ResolvedSkill(
        SkillRecord Record,
        int Version,
        IReadOnlyList<SkillFile> Files,
        string? Content);

    /// <summary>Authorizes the agent token and resolves the skill plus the content of the
    /// requested version. Failure is an IActionResult the caller returns as is: 401 for a
    /// token that does not belong to this session, 404 for a skill this session cannot
    /// read (an unshared or misspelled one — the agent needs to tell those apart).</summary>
    private async Task<(ResolvedSkill? Skill, IActionResult? Failure)> ResolveSkillAsync(
        string id, string nameOrId, int? version, CancellationToken ct)
    {
        if (!Request.Headers.TryGetValue("X-Agent-Token", out var token))
            return (null, Unauthorized());
        var session = await _store.GetByCallbackTokenAsync(token!, ct);
        if (session is null || session.Id != id)
            return (null, Unauthorized());

        SkillRecord record;
        try
        {
            record = await _mcp.ResolveAsync(nameOrId, session, ct);
        }
        catch (ArgumentException e)
        {
            return (null, NotFound(new { error = "skill_not_found", message = e.Message }));
        }
        var effectiveVersion = version ?? record.Version;
        var files = await _skills.GetFilesAsync(record.Id, effectiveVersion, ct);
        var content = effectiveVersion == record.Version
            ? await _skills.GetContentAsync(record, ct)
            : await _skills.GetVersionContentAsync(record.Id, effectiveVersion, ct);
        return (new ResolvedSkill(record, effectiveVersion, files, content), null);
    }

    /// <summary>
    /// Upward sync: the agent pod reports skills it created locally under
    /// ~/.claude/skills (unmanaged directories); unchanged ones are skipped.
    /// Called by the entrypoint after start and on graceful shutdown.
    /// </summary>
    [HttpPost("/internal/sessions/{id}/skills/import")]
    public async Task<IActionResult> Import(
        string id, [FromBody] ImportSkillsRequest request, CancellationToken ct)
    {
        if (!Request.Headers.TryGetValue("X-Agent-Token", out var token))
            return Unauthorized();
        var session = await _store.GetByCallbackTokenAsync(token!, ct);
        if (session is null || session.Id != id)
            return Unauthorized();
        if (request.Skills is null || request.Skills.Count == 0)
            return Ok(new ImportSkillsResult([], [], [], []));
        if (request.Skills.Count > 50)
            return BadRequest(new { error = "Too many skills in one import." });

        return Ok(await _importer.ImportAsync(session, request.Skills, ct));
    }
}
