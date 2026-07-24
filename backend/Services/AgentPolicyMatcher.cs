using System.Text.Json;
using AgentHub.Api.Models;

namespace AgentHub.Api.Services;

public sealed record PolicyDecision(string Decision, string Reason);

public static class AgentPolicyMatcher
{
    public static PolicyDecision Decide(AgentPolicy policy, string tool, JsonElement input)
    {
        if (string.IsNullOrWhiteSpace(tool)) return Deny("Blocked by tool policy.");
        if (string.Equals(tool, "Bash", StringComparison.Ordinal))
            return DecideCommand(policy.AllowedCommands, input);
        if (tool.StartsWith("mcp__", StringComparison.Ordinal))
            return Match(policy.AllowedMcpTools, tool, true) ? Allow() : Deny("Blocked by MCP policy.");
        return Match(policy.AllowedTools, tool, false) ? Allow() : Deny("Blocked by tool policy.");
    }

    private static PolicyDecision DecideCommand(IReadOnlyList<string>? allowed, JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object
            || !input.TryGetProperty("command", out var element)
            || element.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(element.GetString())
            || !TryParse(element.GetString()!, out var components))
            return Deny("Invalid shell command.");

        var prefixes = new List<IReadOnlyList<string>>();
        foreach (var configured in allowed ?? [])
            if (TryParse(configured, out var parsed) && parsed.Count == 1) prefixes.Add(parsed[0]);
        if (prefixes.Count == 0) return Deny("Blocked by command policy.");
        return components.All(component => prefixes.Any(prefix => IsPrefix(prefix, component)))
            ? Allow()
            : Deny("Blocked by command policy.");
    }

    private static bool Match(IReadOnlyList<string>? patterns, string value, bool mcp)
    {
        foreach (var pattern in patterns ?? [])
        {
            if (string.IsNullOrEmpty(pattern)) continue;
            if (!pattern.Contains('*', StringComparison.Ordinal))
            {
                if (string.Equals(pattern, value, StringComparison.Ordinal)) return true;
                continue;
            }
            if (pattern[^1] != '*' || pattern.IndexOf('*') != pattern.Length - 1) continue;
            var prefix = pattern[..^1];
            if (prefix.Length < 3
                || (mcp && (!prefix.StartsWith("mcp__", StringComparison.Ordinal)
                    || !prefix.EndsWith("__", StringComparison.Ordinal)))) continue;
            if (value.StartsWith(prefix, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static bool IsPrefix(IReadOnlyList<string> prefix, IReadOnlyList<string> command)
    {
        if (prefix.Count == 0 || prefix.Count > command.Count) return false;
        for (var index = 0; index < prefix.Count; index++)
            if (!string.Equals(prefix[index], command[index], StringComparison.Ordinal)) return false;
        return true;
    }

    private static bool TryParse(string value, out List<IReadOnlyList<string>> components)
    {
        var parsedComponents = new List<IReadOnlyList<string>>();
        components = parsedComponents;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var current = new List<string>();
        var token = new System.Text.StringBuilder();
        var tokenStarted = false;
        char quote = '\0';

        bool FinishToken()
        {
            if (!tokenStarted) return true;
            var parsed = token.ToString();
            if (IsAssignment(parsed)) return false;
            current.Add(parsed);
            token.Clear();
            tokenStarted = false;
            return true;
        }

        bool FinishComponent()
        {
            if (!FinishToken() || current.Count == 0) return false;
            parsedComponents.Add(current.ToArray());
            current = [];
            return true;
        }

        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character is '\r' or '\n' or '$' or '`' or '\\' or '<' or '>' or '*' or '?' or '[' or ']'
                or '{' or '}' or '(' or ')' or '#' or '!'
                || (char.IsControl(character) && character != '\t')) return false;
            if (quote != '\0')
            {
                if (character == quote) quote = '\0'; else token.Append(character);
                tokenStarted = true;
                continue;
            }
            if (character is '\'' or '"')
            {
                quote = character;
                tokenStarted = true;
                continue;
            }
            if (char.IsWhiteSpace(character))
            {
                if (!FinishToken()) return false;
                continue;
            }
            if (character == ';')
            {
                if (!FinishComponent()) return false;
                continue;
            }
            if (character is '&' or '|')
            {
                if (character == '&' && (index + 1 >= value.Length || value[index + 1] != '&')) return false;
                if (index + 1 < value.Length && value[index + 1] == character) index++;
                if (!FinishComponent()) return false;
                continue;
            }
            token.Append(character);
            tokenStarted = true;
        }
        return quote == '\0' && FinishComponent();
    }

    private static bool IsAssignment(string token)
    {
        var equals = token.IndexOf('=');
        if (equals <= 0 || !(char.IsLetter(token[0]) || token[0] == '_')) return false;
        for (var index = 1; index < equals; index++)
            if (!(char.IsLetterOrDigit(token[index]) || token[index] == '_')) return false;
        return true;
    }

    private static PolicyDecision Allow() => new("allow", "Allowed by session policy.");
    private static PolicyDecision Deny(string reason) => new("deny", reason);
}
