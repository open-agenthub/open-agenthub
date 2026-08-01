using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentHub.Api.Library.ApiMcpGateway;

/// <summary>One OpenAPI operation exposed as an MCP tool.</summary>
public sealed record OpenApiMappedTool(
    string Name,
    string Description,
    string Method,
    string PathTemplate,
    IReadOnlyList<OpenApiParamBinding> Parameters,
    bool HasJsonBody,
    JsonElement InputSchema);

public sealed record OpenApiParamBinding(string Name, string In, bool Required);

/// <summary>
/// Maps OpenAPI 3 path operations to MCP tools (one tool per operation).
/// Body fields are flattened into the tool input schema alongside path/query/header params.
/// </summary>
public static class OpenApiToolMapper
{
    private static readonly string[] HttpMethods =
        ["get", "put", "post", "delete", "patch", "head", "options"];

    public static IReadOnlyList<OpenApiMappedTool> MapTools(string openApiJson)
    {
        using var doc = JsonDocument.Parse(openApiJson);
        var root = doc.RootElement;
        if (!root.TryGetProperty("paths", out var paths) || paths.ValueKind != JsonValueKind.Object)
            return [];

        var tools = new List<OpenApiMappedTool>();
        foreach (var pathProp in paths.EnumerateObject())
        {
            if (pathProp.Value.ValueKind != JsonValueKind.Object)
                continue;
            foreach (var methodProp in pathProp.Value.EnumerateObject())
            {
                if (!HttpMethods.Contains(methodProp.Name, StringComparer.OrdinalIgnoreCase))
                    continue;
                if (methodProp.Value.ValueKind != JsonValueKind.Object)
                    continue;
                tools.Add(MapOperation(root, pathProp.Name, methodProp.Name.ToUpperInvariant(), methodProp.Value));
            }
        }

        return tools;
    }

    public static string? ReadDefaultBaseUrl(string openApiJson)
    {
        using var doc = JsonDocument.Parse(openApiJson);
        if (!doc.RootElement.TryGetProperty("servers", out var servers)
            || servers.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var server in servers.EnumerateArray())
        {
            if (server.TryGetProperty("url", out var url)
                && url.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(url.GetString()))
            {
                return url.GetString()!.TrimEnd('/');
            }
        }
        return null;
    }

    private static OpenApiMappedTool MapOperation(
        JsonElement root, string path, string method, JsonElement op)
    {
        var name = op.TryGetProperty("operationId", out var opId)
                   && opId.ValueKind == JsonValueKind.String
                   && !string.IsNullOrWhiteSpace(opId.GetString())
            ? SanitizeToolName(opId.GetString()!)
            : SanitizeToolName($"{method}_{path}");

        var description = op.TryGetProperty("summary", out var summary)
                          && summary.ValueKind == JsonValueKind.String
                          && !string.IsNullOrWhiteSpace(summary.GetString())
            ? summary.GetString()!
            : op.TryGetProperty("description", out var desc)
              && desc.ValueKind == JsonValueKind.String
                ? desc.GetString() ?? $"{method} {path}"
                : $"{method} {path}";

        var bindings = new List<OpenApiParamBinding>();
        var properties = new JsonObject();
        var required = new JsonArray();

        if (op.TryGetProperty("parameters", out var parameters)
            && parameters.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in parameters.EnumerateArray())
            {
                if (!p.TryGetProperty("name", out var nameEl)
                    || nameEl.ValueKind != JsonValueKind.String)
                    continue;
                var paramName = nameEl.GetString()!;
                var loc = p.TryGetProperty("in", out var inEl) && inEl.ValueKind == JsonValueKind.String
                    ? inEl.GetString()!.ToLowerInvariant()
                    : "query";
                var isRequired = p.TryGetProperty("required", out var reqEl)
                                 && reqEl.ValueKind == JsonValueKind.True;
                bindings.Add(new OpenApiParamBinding(paramName, loc, isRequired));

                JsonNode schemaNode = p.TryGetProperty("schema", out var schema)
                    ? JsonNode.Parse(ResolveRef(root, schema).GetRawText())
                      ?? new JsonObject { ["type"] = "string" }
                    : new JsonObject { ["type"] = "string" };
                properties[paramName] = schemaNode;
                if (isRequired)
                    required.Add(paramName);
            }
        }

        var hasJsonBody = false;
        if (op.TryGetProperty("requestBody", out var body)
            && body.ValueKind == JsonValueKind.Object
            && body.TryGetProperty("content", out var content)
            && content.ValueKind == JsonValueKind.Object
            && content.TryGetProperty("application/json", out var jsonContent)
            && jsonContent.ValueKind == JsonValueKind.Object
            && jsonContent.TryGetProperty("schema", out var bodySchemaRaw))
        {
            hasJsonBody = true;
            var bodySchema = ResolveRef(root, bodySchemaRaw);
            var bodyRequired = body.TryGetProperty("required", out var bodyReq)
                               && bodyReq.ValueKind == JsonValueKind.True;

            if (bodySchema.ValueKind == JsonValueKind.Object
                && bodySchema.TryGetProperty("properties", out var bodyProps)
                && bodyProps.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in bodyProps.EnumerateObject())
                    properties[prop.Name] = JsonNode.Parse(prop.Value.GetRawText());

                if (bodySchema.TryGetProperty("required", out var bodyRequiredNames)
                    && bodyRequiredNames.ValueKind == JsonValueKind.Array)
                {
                    foreach (var r in bodyRequiredNames.EnumerateArray())
                    {
                        if (r.ValueKind == JsonValueKind.String)
                            required.Add(r.GetString()!);
                    }
                }
            }
            else
            {
                properties["body"] = JsonNode.Parse(bodySchema.GetRawText());
                if (bodyRequired)
                    required.Add("body");
                bindings.Add(new OpenApiParamBinding("body", "body", bodyRequired));
            }
        }

        var schemaObj = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties
        };
        if (required.Count > 0)
            schemaObj["required"] = required;

        using var schemaDoc = JsonDocument.Parse(schemaObj.ToJsonString());
        return new OpenApiMappedTool(
            name,
            description,
            method,
            path,
            bindings,
            hasJsonBody,
            schemaDoc.RootElement.Clone());
    }

    /// <summary>Resolves local <c>#/components/...</c> refs (one level; enough for v1 fixtures).</summary>
    private static JsonElement ResolveRef(JsonElement root, JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object
            || !schema.TryGetProperty("$ref", out var refEl)
            || refEl.ValueKind != JsonValueKind.String)
            return schema;

        var refPath = refEl.GetString();
        if (string.IsNullOrEmpty(refPath) || !refPath.StartsWith("#/", StringComparison.Ordinal))
            return schema;

        var current = root;
        foreach (var segment in refPath[2..].Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!current.TryGetProperty(segment, out current))
                return schema;
        }
        return current;
    }

    internal static string SanitizeToolName(string raw)
    {
        var chars = raw.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_').ToArray();
        var name = new string(chars).Trim('_');
        return string.IsNullOrEmpty(name) ? "operation" : name;
    }
}
