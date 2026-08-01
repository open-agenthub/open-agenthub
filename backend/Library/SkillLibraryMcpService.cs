using System.Text.Json;
using System.Text.Json.Nodes;
using AgentHub.Api.Persistence;

namespace AgentHub.Api.Library;

/// <summary>Builds the injected .mcp.json entry for the built-in skill-library server:
/// the agent talks back to the hub over the callback service, authenticated with the
/// session's callback token.</summary>
public static class SkillLibraryMcpConfig
{
    public static McpServerRecord BuildServer(string callbackBaseUrl, SessionRecord session) => new()
    {
        Id = "builtin-skill-library",
        Owner = session.Owner,
        Name = SkillLibraryMcpService.ServerName,
        ConfigJson = JsonSerializer.Serialize(new
        {
            type = "http",
            url = $"{callbackBaseUrl.TrimEnd('/')}/internal/sessions/{session.Id}/mcp",
            headers = new Dictionary<string, string> { ["X-Agent-Token"] = session.CallbackToken }
        })
    };
}

/// <summary>
/// The skill-library MCP server that agent sessions talk to (Streamable HTTP,
/// JSON responses only — no SSE stream is needed for these short calls).
/// Lets an agent search, read, upload and version skills. Everything is scoped
/// to the session: uploads land in the session's project (or the personal
/// library when the session has none), reads cover project + personal + shared.
/// </summary>
public sealed class SkillLibraryMcpService
{
    public const string ServerName = "skill-library";
    private const string LatestProtocol = "2025-06-18";
    private static readonly string[] KnownProtocols = ["2024-11-05", "2025-03-26", "2025-06-18"];

    private readonly ILibraryAccess _library;
    private readonly ISkillStore _skills;
    private readonly SkillSearchService _search;

    public SkillLibraryMcpService(ILibraryAccess library, ISkillStore skills, SkillSearchService search)
    {
        _library = library;
        _skills = skills;
        _search = search;
    }

    /// <summary>Handles one JSON-RPC message; null means "no body" (notification → 202).</summary>
    public async Task<JsonObject?> HandleAsync(JsonElement message, SessionRecord session, CancellationToken ct)
    {
        if (message.ValueKind != JsonValueKind.Object)
            return Error(null, -32600, "Expected a single JSON-RPC message.");

        JsonNode? id = message.TryGetProperty("id", out var idEl)
            ? JsonNode.Parse(idEl.GetRawText())
            : null;
        var method = message.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String
            ? m.GetString() ?? ""
            : "";
        var hasParams = message.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Object;

        if (method.StartsWith("notifications/", StringComparison.Ordinal))
            return null;

        try
        {
            return method switch
            {
                "initialize" => Result(id, Initialize(hasParams ? p : default)),
                "ping" => Result(id, new JsonObject()),
                "tools/list" => Result(id, ToolsList()),
                "tools/call" => Result(id, await ToolsCallAsync(hasParams ? p : default, session, ct)),
                _ => Error(id, -32601, $"Method not found: {method}"),
            };
        }
        catch (ArgumentException e)
        {
            return Result(id, ToolError(e.Message));
        }
    }

    private static JsonObject Initialize(JsonElement @params)
    {
        var requested = @params.ValueKind == JsonValueKind.Object
                        && @params.TryGetProperty("protocolVersion", out var v)
                        && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? LatestProtocol
            : LatestProtocol;
        return new JsonObject
        {
            ["protocolVersion"] = KnownProtocols.Contains(requested) ? requested : LatestProtocol,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = "agenthub-skill-library",
                ["title"] = "AgentHub Skill Library",
                ["version"] = "1.0.0"
            },
            ["instructions"] =
                "Search, read, upload and version reusable agent skills (SKILL.md documents) " +
                "in this AgentHub instance. Uploads are scoped to the session's project when it has one."
        };
    }

    // ------------------------------------------------------------------ tools

    private static JsonObject ToolsList() => new()
    {
        ["tools"] = new JsonArray(
            Tool("search_skills",
                "Search the skill library (project, personal and shared skills) by meaning and keywords. " +
                "Returns the best matches with id, name, description and version.",
                new JsonObject
                {
                    ["query"] = Prop("string", "What you are looking for."),
                    ["limit"] = Prop("integer", "Maximum number of results (default 10, max 50).")
                }, required: ["query"]),
            Tool("get_skill",
                "Read a skill's SKILL.md content by name (or id). Optionally a specific version.",
                new JsonObject
                {
                    ["name"] = Prop("string", "Skill name (kebab-case) or skill id."),
                    ["version"] = Prop("integer", "Specific version to read; omit for the latest.")
                }, required: ["name"]),
            Tool("upload_skill",
                "Create or update a skill. Saves a new immutable version; the previous content stays " +
                "in the history. The skill lands in this session's project, or in the personal library " +
                "when the session has no project.",
                new JsonObject
                {
                    ["name"] = Prop("string", "Skill name: lowercase letters, digits and hyphens."),
                    ["description"] = Prop("string", "One-line description of when to use the skill."),
                    ["content"] = Prop("string", "The full SKILL.md markdown content."),
                    ["comment"] = Prop("string", "Optional change note (what changed and why).")
                }, required: ["name", "content"]),
            Tool("list_skill_versions",
                "List the version history of a skill (newest first).",
                new JsonObject
                {
                    ["name"] = Prop("string", "Skill name (kebab-case) or skill id.")
                }, required: ["name"]),
            Tool("restore_skill_version",
                "Restore an older version of one of your skills as the new latest version.",
                new JsonObject
                {
                    ["name"] = Prop("string", "Skill name (kebab-case) or skill id."),
                    ["version"] = Prop("integer", "The version number to restore.")
                }, required: ["name", "version"]))
    };

    private async Task<JsonObject> ToolsCallAsync(
        JsonElement @params, SessionRecord session, CancellationToken ct)
    {
        if (@params.ValueKind != JsonValueKind.Object
            || !@params.TryGetProperty("name", out var nameEl)
            || nameEl.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException("tools/call requires a tool name.");
        }
        var args = @params.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.Object
            ? a
            : default;

        return nameEl.GetString() switch
        {
            "search_skills" => await SearchAsync(args, session, ct),
            "get_skill" => await GetAsync(args, session, ct),
            "upload_skill" => await UploadAsync(args, session, ct),
            "list_skill_versions" => await ListVersionsAsync(args, session, ct),
            "restore_skill_version" => await RestoreAsync(args, session, ct),
            var unknown => throw new ArgumentException($"Unknown tool: {unknown}"),
        };
    }

    private async Task<JsonObject> SearchAsync(
        JsonElement args, SessionRecord session, CancellationToken ct)
    {
        var query = RequireString(args, "query");
        var limit = OptionalInt(args, "limit") ?? SkillSearchService.DefaultLimit;
        var accessible = await _library.ListAccessibleSkillsAsync(session.Owner, session.ProjectId, ct);
        var hits = await _search.SearchAsync(session.Owner, accessible, query, limit, ct);
        return ToolResult(new JsonObject
        {
            ["total"] = hits.Count,
            ["results"] = new JsonArray(hits.Select(h => (JsonNode)new JsonObject
            {
                ["id"] = h.Id,
                ["name"] = h.Name,
                ["description"] = h.Description,
                ["version"] = h.Version,
                ["scope"] = !h.Mine ? "shared" : h.ProjectId is null ? "personal" : "project",
                ["score"] = h.Score
            }).ToArray())
        });
    }

    private async Task<JsonObject> GetAsync(
        JsonElement args, SessionRecord session, CancellationToken ct)
    {
        var record = await ResolveAsync(RequireString(args, "name"), session, ct);
        var version = OptionalInt(args, "version");
        var content = version is null || version == record.Version
            ? await _skills.GetContentAsync(record, ct)
            : await _skills.GetVersionContentAsync(record.Id, version.Value, ct);
        if (content is null)
            throw new ArgumentException(version is null
                ? $"Skill '{record.Name}' has no content."
                : $"Skill '{record.Name}' has no version {version}.");
        return ToolResult(new JsonObject
        {
            ["id"] = record.Id,
            ["name"] = record.Name,
            ["description"] = record.Description,
            ["version"] = version ?? record.Version,
            ["content"] = content
        });
    }

    private async Task<JsonObject> UploadAsync(
        JsonElement args, SessionRecord session, CancellationToken ct)
    {
        var name = LibraryValidation.ValidateSkillName(RequireString(args, "name"));
        var request = new SaveSkillRequest(
            name,
            OptionalString(args, "description"),
            RequireString(args, "content"),
            ProjectId: session.ProjectId,
            Comment: OptionalString(args, "comment"),
            SavedBy: $"session:{session.Id}");

        // Upsert within the session's scope; a same-named skill in another
        // scope (other project, shared by someone else) is not touched.
        var own = await _skills.ListByOwnerAsync(session.Owner, ct);
        var existing = own.FirstOrDefault(r => r.Name == name && r.ProjectId == session.ProjectId);
        var record = existing is null
            ? await _skills.CreateAsync(session.Owner, request, ct)
            : await _skills.UpdateAsync(session.Owner, existing.Id,
                request with { Description = OptionalString(args, "description") ?? existing.Description }, ct);
        await _search.IndexAsync(record, request.Content, ct);

        return ToolResult(new JsonObject
        {
            ["id"] = record.Id,
            ["name"] = record.Name,
            ["version"] = record.Version,
            ["scope"] = record.ProjectId is null ? "personal" : "project",
            ["created"] = existing is null
        });
    }

    private async Task<JsonObject> ListVersionsAsync(
        JsonElement args, SessionRecord session, CancellationToken ct)
    {
        var record = await ResolveAsync(RequireString(args, "name"), session, ct);
        var versions = await _skills.ListVersionsAsync(record.Id, ct);
        return ToolResult(new JsonObject
        {
            ["id"] = record.Id,
            ["name"] = record.Name,
            ["latest"] = record.Version,
            ["versions"] = new JsonArray(versions.Select(v => (JsonNode)new JsonObject
            {
                ["version"] = v.Version,
                ["comment"] = v.Comment,
                ["createdBy"] = v.CreatedBy,
                ["createdAt"] = v.CreatedAt.ToString("O")
            }).ToArray())
        });
    }

    private async Task<JsonObject> RestoreAsync(
        JsonElement args, SessionRecord session, CancellationToken ct)
    {
        var record = await ResolveAsync(RequireString(args, "name"), session, ct);
        if (record.Owner != session.Owner)
            throw new ArgumentException("Only your own skills can be restored.");
        var version = OptionalInt(args, "version")
            ?? throw new ArgumentException("Argument 'version' is required.");
        try
        {
            var restored = await _skills.RestoreVersionAsync(
                session.Owner, record.Id, version, $"session:{session.Id}", ct);
            var content = await _skills.GetContentAsync(restored, ct) ?? "";
            await _search.IndexAsync(restored, content, ct);
            return ToolResult(new JsonObject
            {
                ["id"] = restored.Id,
                ["name"] = restored.Name,
                ["version"] = restored.Version,
                ["restoredFrom"] = version
            });
        }
        catch (KeyNotFoundException)
        {
            throw new ArgumentException($"Skill '{record.Name}' has no version {version}.");
        }
    }

    /// <summary>Resolves a tool's name argument to an accessible skill —
    /// by exact name (project beats personal beats shared) or by id.</summary>
    private async Task<SkillRecord> ResolveAsync(
        string nameOrId, SessionRecord session, CancellationToken ct)
    {
        var accessible = await _library.ListAccessibleSkillsAsync(session.Owner, session.ProjectId, ct);
        var record = accessible
            .Where(r => r.Name == nameOrId)
            .OrderBy(r => r.Owner != session.Owner ? 2 : r.ProjectId is null ? 1 : 0)
            .FirstOrDefault()
            ?? accessible.FirstOrDefault(r => r.Id == nameOrId);
        return record ?? throw new ArgumentException($"No accessible skill named '{nameOrId}'.");
    }

    // ------------------------------------------------------------ JSON-RPC plumbing

    private static JsonObject Result(JsonNode? id, JsonObject result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["result"] = result
    };

    private static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
    };

    private static JsonObject ToolResult(JsonObject payload) => new()
    {
        ["content"] = new JsonArray(new JsonObject
        {
            ["type"] = "text",
            ["text"] = payload.ToJsonString(new JsonSerializerOptions { WriteIndented = true })
        })
    };

    private static JsonObject ToolError(string message) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = message }),
        ["isError"] = true
    };

    private static JsonObject Tool(string name, string description, JsonObject properties, string[] required) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray())
        }
    };

    private static JsonObject Prop(string type, string description) => new()
    {
        ["type"] = type,
        ["description"] = description
    };

    private static string RequireString(JsonElement args, string name)
    {
        if (args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
            && value.GetString() is { Length: > 0 } s)
        {
            return s;
        }
        throw new ArgumentException($"Argument '{name}' is required.");
    }

    private static string? OptionalString(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object
        && args.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? OptionalInt(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object
        && args.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : null;
}
