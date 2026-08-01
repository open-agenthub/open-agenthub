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
                ["version"] = "1.1.0"
            },
            ["instructions"] =
                "This AgentHub instance keeps a persistent, versioned library of agent skills " +
                "(SKILL.md documents, optionally with helper scripts and other files). Use it " +
                "proactively, without waiting to be asked:\n" +
                "- BEFORE starting a non-trivial task, call search_skills — a proven runbook, " +
                "checklist or script for it may already exist.\n" +
                "- AFTER you worked out a reusable procedure, fixed something in a non-obvious " +
                "way, or wrote a helper script worth keeping, save it with upload_skill (or " +
                "update the existing skill) so future sessions benefit. Offer this to the user " +
                "when unsure; for clearly reusable knowledge just do it and mention it.\n" +
                "- Skills you create as directories under ~/.claude/skills are also picked up " +
                "automatically when the session ends, but upload_skill makes them available " +
                "immediately and lets you add a change comment.\n" +
                "Every save creates a new version; nothing is lost — old versions can be listed " +
                "and restored. Uploads are scoped to this session's project when it has one, " +
                "otherwise to the owner's personal library."
        };
    }

    // ------------------------------------------------------------------ tools

    private static JsonObject ToolsList() => new()
    {
        ["tools"] = new JsonArray(
            Tool("search_skills",
                "Search the persistent skill library (project, personal and shared skills) by meaning " +
                "and keywords. Use this proactively BEFORE starting a non-trivial task — a proven " +
                "runbook, checklist or helper script may already exist. Returns the best matches with " +
                "id, name, description and version.",
                new JsonObject
                {
                    ["query"] = Prop("string", "What you are looking for."),
                    ["limit"] = Prop("integer", "Maximum number of results (default 10, max 50).")
                }, required: ["query"]),
            Tool("get_skill",
                "Read a skill: its SKILL.md content and the list of extra files (scripts, templates). " +
                "Pass 'file' to read one of those files, 'version' for an older revision.",
                new JsonObject
                {
                    ["name"] = Prop("string", "Skill name (kebab-case) or skill id."),
                    ["version"] = Prop("integer", "Specific version to read; omit for the latest."),
                    ["file"] = Prop("string", "Path of an extra file to read (e.g. scripts/check.sh); omit for SKILL.md.")
                }, required: ["name"]),
            Tool("upload_skill",
                "Save a skill to the persistent library — create it or update the existing one. Use this " +
                "proactively whenever you have worked out a reusable procedure, a non-obvious fix, a " +
                "checklist, or helper scripts worth keeping: future sessions (yours and this project's) " +
                "will find them via search_skills. Saving is safe — every upload creates a new immutable " +
                "version and older versions stay restorable. The skill lands in this session's project, " +
                "or in the personal library when the session has no project.",
                new JsonObject
                {
                    ["name"] = Prop("string", "Skill name: lowercase letters, digits and hyphens (e.g. deploy-runbook)."),
                    ["description"] = Prop("string", "One line on when the agent should use this skill — important for discovery."),
                    ["content"] = Prop("string", "The full SKILL.md markdown content (frontmatter with name/description recommended)."),
                    ["comment"] = Prop("string", "Optional change note (what changed and why) for the version history."),
                    ["files"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["description"] = "Extra files stored next to SKILL.md — helper scripts, templates, reference docs. " +
                            "Replaces the previous file set when given; omit to keep the existing files.",
                        ["items"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["path"] = Prop("string", "Relative path, e.g. scripts/check.sh (max depth 3)."),
                                ["content"] = Prop("string", "Text content of the file.")
                            },
                            ["required"] = new JsonArray("path", "content")
                        }
                    }
                }, required: ["name", "content"]),
            Tool("list_skill_versions",
                "List the version history of a skill (newest first), with author and change comments.",
                new JsonObject
                {
                    ["name"] = Prop("string", "Skill name (kebab-case) or skill id.")
                }, required: ["name"]),
            Tool("restore_skill_version",
                "Restore an older version of one of your skills (content and files) as the new latest version.",
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
        var version = OptionalInt(args, "version") ?? record.Version;
        var files = await _skills.GetFilesAsync(record.Id, version, ct);

        if (OptionalString(args, "file") is { Length: > 0 } filePath)
        {
            var file = files.FirstOrDefault(f => f.Path == filePath)
                ?? throw new ArgumentException(
                    $"Skill '{record.Name}' (v{version}) has no file '{filePath}'. " +
                    $"Available: {(files.Count == 0 ? "none" : string.Join(", ", files.Select(f => f.Path)))}");
            return ToolResult(new JsonObject
            {
                ["id"] = record.Id,
                ["name"] = record.Name,
                ["version"] = version,
                ["file"] = file.Path,
                ["content"] = file.Content
            });
        }

        var content = version == record.Version
            ? await _skills.GetContentAsync(record, ct)
            : await _skills.GetVersionContentAsync(record.Id, version, ct);
        if (content is null)
            throw new ArgumentException(version == record.Version
                ? $"Skill '{record.Name}' has no content."
                : $"Skill '{record.Name}' has no version {version}.");
        return ToolResult(new JsonObject
        {
            ["id"] = record.Id,
            ["name"] = record.Name,
            ["description"] = record.Description,
            ["version"] = version,
            ["content"] = content,
            ["files"] = new JsonArray(files.Select(f => (JsonNode)f.Path).ToArray())
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
            SavedBy: $"session:{session.Id}",
            Files: ParseFiles(args));

        // Upsert within the session's scope; a same-named skill in another
        // scope (other project, shared by someone else) is not touched.
        var own = await _skills.ListByOwnerAsync(session.Owner, ct);
        var existing = own.FirstOrDefault(r => r.Name == name && r.ProjectId == session.ProjectId);
        var record = existing is null
            ? await _skills.CreateAsync(session.Owner, request, ct)
            : await _skills.UpdateAsync(session.Owner, existing.Id,
                request with { Description = OptionalString(args, "description") ?? existing.Description }, ct);
        await _search.IndexAsync(record, request.Content, ct);

        var storedFiles = await _skills.GetFilesAsync(record.Id, record.Version, ct);
        return ToolResult(new JsonObject
        {
            ["id"] = record.Id,
            ["name"] = record.Name,
            ["version"] = record.Version,
            ["scope"] = record.ProjectId is null ? "personal" : "project",
            ["created"] = existing is null,
            ["files"] = new JsonArray(storedFiles.Select(f => (JsonNode)f.Path).ToArray())
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

    /// <summary>Parses the optional files argument; null = keep existing files.</summary>
    private static IReadOnlyList<SkillFile>? ParseFiles(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty("files", out var value))
        {
            return null;
        }
        if (value.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("'files' must be an array of {path, content} objects.");
        var files = new List<SkillFile>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("'files' must be an array of {path, content} objects.");
            files.Add(new SkillFile(RequireString(item, "path"), RequireString(item, "content")));
        }
        return files;
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
