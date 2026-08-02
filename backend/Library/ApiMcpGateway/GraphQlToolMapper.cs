using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgentHub.Api.Library.ApiMcpGateway;

public enum GraphQlOperationKind
{
    Query,
    Mutation
}

/// <summary>One GraphQL root field exposed as an MCP tool.</summary>
public sealed record GraphQlMappedTool(
    string Name,
    string Description,
    GraphQlOperationKind Kind,
    string FieldName,
    IReadOnlyList<GraphQlArgBinding> Arguments,
    string ReturnTypeName,
    bool ReturnIsScalar,
    JsonElement InputSchema);

public sealed record GraphQlArgBinding(string Name, string TypeName, bool Required);

/// <summary>
/// Maps GraphQL Query/Mutation fields to MCP tools (one tool per root field).
/// Supports SDL schema documents and introspection JSON.
/// </summary>
public static partial class GraphQlToolMapper
{
    private static readonly HashSet<string> ScalarNames = new(StringComparer.Ordinal)
    {
        "ID", "String", "Int", "Float", "Boolean"
    };

    public static IReadOnlyList<GraphQlMappedTool> MapToolsFromSdl(string sdl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sdl);
        var tools = new List<GraphQlMappedTool>();
        tools.AddRange(ParseSdlTypeFields(sdl, "Query", GraphQlOperationKind.Query));
        tools.AddRange(ParseSdlTypeFields(sdl, "Mutation", GraphQlOperationKind.Mutation));
        return tools;
    }

    public static IReadOnlyList<GraphQlMappedTool> MapToolsFromIntrospection(string introspectionJson)
    {
        using var doc = JsonDocument.Parse(introspectionJson);
        var root = doc.RootElement;
        if (root.TryGetProperty("data", out var data))
            root = data;
        if (!root.TryGetProperty("__schema", out var schema))
            return [];

        string? queryType = null;
        string? mutationType = null;
        if (schema.TryGetProperty("queryType", out var qt)
            && qt.ValueKind == JsonValueKind.Object
            && qt.TryGetProperty("name", out var qn)
            && qn.ValueKind == JsonValueKind.String)
            queryType = qn.GetString();
        if (schema.TryGetProperty("mutationType", out var mt)
            && mt.ValueKind == JsonValueKind.Object
            && mt.TryGetProperty("name", out var mn)
            && mn.ValueKind == JsonValueKind.String)
            mutationType = mn.GetString();

        var types = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (schema.TryGetProperty("types", out var typesEl) && typesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in typesEl.EnumerateArray())
            {
                if (t.TryGetProperty("name", out var nameEl)
                    && nameEl.ValueKind == JsonValueKind.String
                    && !string.IsNullOrEmpty(nameEl.GetString()))
                    types[nameEl.GetString()!] = t;
            }
        }

        var tools = new List<GraphQlMappedTool>();
        if (queryType is not null && types.TryGetValue(queryType, out var queryObj))
            tools.AddRange(MapIntrospectionFields(queryObj, GraphQlOperationKind.Query));
        if (mutationType is not null && types.TryGetValue(mutationType, out var mutationObj))
            tools.AddRange(MapIntrospectionFields(mutationObj, GraphQlOperationKind.Mutation));
        return tools;
    }

    /// <summary>Detect SDL vs introspection JSON and map tools.</summary>
    public static IReadOnlyList<GraphQlMappedTool> MapTools(string document)
    {
        var trimmed = document.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
            return MapToolsFromIntrospection(document);
        return MapToolsFromSdl(document);
    }

    public const string IntrospectionQuery = """
        query IntrospectionQuery {
          __schema {
            queryType { name }
            mutationType { name }
            types {
              kind
              name
              fields {
                name
                description
                args {
                  name
                  type { ...TypeRef }
                }
                type { ...TypeRef }
              }
            }
          }
        }
        fragment TypeRef on __Type {
          kind
          name
          ofType {
            kind
            name
            ofType {
              kind
              name
              ofType {
                kind
                name
                ofType {
                  kind
                  name
                  ofType {
                    kind
                    name
                    ofType {
                      kind
                      name
                      ofType { kind name }
                    }
                  }
                }
              }
            }
          }
        }
        """;

    private static IEnumerable<GraphQlMappedTool> MapIntrospectionFields(
        JsonElement typeObj, GraphQlOperationKind kind)
    {
        if (!typeObj.TryGetProperty("fields", out var fields)
            || fields.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var field in fields.EnumerateArray())
        {
            if (!field.TryGetProperty("name", out var nameEl)
                || nameEl.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(nameEl.GetString()))
                continue;

            var fieldName = nameEl.GetString()!;
            var description = field.TryGetProperty("description", out var descEl)
                              && descEl.ValueKind == JsonValueKind.String
                              && !string.IsNullOrWhiteSpace(descEl.GetString())
                ? descEl.GetString()!
                : $"{kind.ToString().ToLowerInvariant()} {fieldName}";

            var args = new List<GraphQlArgBinding>();
            var properties = new JsonObject();
            var required = new JsonArray();

            if (field.TryGetProperty("args", out var argsEl) && argsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var arg in argsEl.EnumerateArray())
                {
                    if (!arg.TryGetProperty("name", out var an)
                        || an.ValueKind != JsonValueKind.String)
                        continue;
                    var argName = an.GetString()!;
                    var (typeName, isRequired) = UnwrapIntrospectionType(
                        arg.TryGetProperty("type", out var at) ? at : default);
                    args.Add(new GraphQlArgBinding(argName, typeName, isRequired));
                    properties[argName] = new JsonObject { ["type"] = MapScalarToJson(typeName) };
                    if (isRequired)
                        required.Add(argName);
                }
            }

            var (returnType, _) = UnwrapIntrospectionType(
                field.TryGetProperty("type", out var rt) ? rt : default);
            var returnIsScalar = ScalarNames.Contains(returnType);

            yield return BuildTool(fieldName, description, kind, args, properties, required,
                returnType, returnIsScalar);
        }
    }

    private static (string Name, bool Required) UnwrapIntrospectionType(JsonElement type)
    {
        var required = false;
        var current = type;
        for (var i = 0; i < 16 && current.ValueKind == JsonValueKind.Object; i++)
        {
            var kind = current.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String
                ? k.GetString()!
                : "";
            if (kind == "NON_NULL")
            {
                required = true;
                if (!current.TryGetProperty("ofType", out current))
                    break;
                continue;
            }

            if (kind is "LIST")
            {
                if (!current.TryGetProperty("ofType", out current))
                    break;
                continue;
            }

            if (current.TryGetProperty("name", out var name)
                && name.ValueKind == JsonValueKind.String
                && !string.IsNullOrEmpty(name.GetString()))
                return (name.GetString()!, required);

            break;
        }

        return ("String", required);
    }

    private static IEnumerable<GraphQlMappedTool> ParseSdlTypeFields(
        string sdl, string typeName, GraphQlOperationKind kind)
    {
        // Match `type Query { ... }` / `type Mutation { ... }` bodies (non-greedy).
        var typeMatch = Regex.Match(
            sdl,
            $@"type\s+{Regex.Escape(typeName)}\s*\{{(?<body>.*?)\n\}}",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);
        if (!typeMatch.Success)
            yield break;

        var body = typeMatch.Groups["body"].Value;
        // Optional description string immediately before a field.
        var fieldRegex = FieldRegex();
        foreach (Match m in fieldRegex.Matches(body))
        {
            var desc = m.Groups["desc"].Success
                ? m.Groups["desc"].Value.Trim().Trim('"')
                : $"{kind.ToString().ToLowerInvariant()} {m.Groups["name"].Value}";
            var fieldName = m.Groups["name"].Value;
            var argsRaw = m.Groups["args"].Success ? m.Groups["args"].Value : "";
            var returnRaw = m.Groups["ret"].Value.Trim();

            var args = new List<GraphQlArgBinding>();
            var properties = new JsonObject();
            var required = new JsonArray();
            if (!string.IsNullOrWhiteSpace(argsRaw))
            {
                foreach (var part in SplitArgs(argsRaw))
                {
                    var colon = part.IndexOf(':');
                    if (colon <= 0) continue;
                    var argName = part[..colon].Trim();
                    var typeExpr = part[(colon + 1)..].Trim();
                    var isRequired = typeExpr.EndsWith('!');
                    var typeNameOnly = StripListNull(typeExpr);
                    args.Add(new GraphQlArgBinding(argName, typeNameOnly, isRequired));
                    properties[argName] = new JsonObject { ["type"] = MapScalarToJson(typeNameOnly) };
                    if (isRequired)
                        required.Add(argName);
                }
            }

            var returnType = StripListNull(returnRaw);
            var returnIsScalar = ScalarNames.Contains(returnType);
            yield return BuildTool(fieldName, desc, kind, args, properties, required,
                returnType, returnIsScalar);
        }
    }

    private static GraphQlMappedTool BuildTool(
        string fieldName,
        string description,
        GraphQlOperationKind kind,
        List<GraphQlArgBinding> args,
        JsonObject properties,
        JsonArray required,
        string returnType,
        bool returnIsScalar)
    {
        // Optional selection set override for object returns.
        if (!returnIsScalar)
            properties["_selection"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "GraphQL selection set for the field result (default: { __typename })"
            };

        var schemaObj = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties
        };
        if (required.Count > 0)
            schemaObj["required"] = required;

        using var schemaDoc = JsonDocument.Parse(schemaObj.ToJsonString());
        return new GraphQlMappedTool(
            OpenApiToolMapper.SanitizeToolName(fieldName),
            description,
            kind,
            fieldName,
            args,
            returnType,
            returnIsScalar,
            schemaDoc.RootElement.Clone());
    }

    private static IEnumerable<string> SplitArgs(string argsRaw)
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i < argsRaw.Length; i++)
        {
            var c = argsRaw[i];
            if (c is '[' or '(' or '{') depth++;
            else if (c is ']' or ')' or '}') depth = Math.Max(0, depth - 1);
            else if (c == ',' && depth == 0)
            {
                yield return argsRaw[start..i].Trim();
                start = i + 1;
            }
        }

        var last = argsRaw[start..].Trim();
        if (last.Length > 0)
            yield return last;
    }

    private static string StripListNull(string typeExpr)
    {
        var s = typeExpr.Trim().TrimEnd('!');
        while (s.StartsWith('[') && s.EndsWith(']'))
            s = s[1..^1].Trim().TrimEnd('!');
        return s;
    }

    private static string MapScalarToJson(string graphqlType) => graphqlType switch
    {
        "Int" or "Float" => "number",
        "Boolean" => "boolean",
        _ => "string"
    };

    [GeneratedRegex(
        @"(?:""""""(?<desc>.*?)""""""|""(?<desc>[^""]*)"")?\s*(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*(?:\((?<args>[^)]*)\))?\s*:\s*(?<ret>[^\n]+)",
        RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex FieldRegex();
}
