using System.Text.Json;
using AgentHub.Api.Models;

namespace AgentHub.Api.Services;

public sealed record PolicyDecision(string Decision, string Reason);

public static class AgentPolicyMatcher
{
    /// <summary>
    /// Decides whether a session's stored policy covers one tool call: "allow" when the allow
    /// list names it, "deny" when it does not or the input cannot be read, and "ask" when the
    /// call is uncovered but the session auto-approves and the approval endpoint should answer.
    /// </summary>
    /// <param name="autoApprove">
    /// The session approves tool requests on its own. A tool the allow list does not cover is
    /// then "ask" rather than "deny", so the runtime hook goes on to the permission endpoint
    /// where auto-approve answers it — the allow list is a head start, not the boundary, which
    /// is how the Claude runtime has always treated it. Without this an unattended session was
    /// held to its allow list no matter what its auto-approve flag said, and an empty list meant
    /// the agent was denied its first tool call and finished having done nothing.
    ///
    /// Malformed input is never softened this way: an unparseable shell command stays a hard
    /// deny, because "we could not tell what this command does" is not a question anyone can be
    /// asked. The live MCP sharing policy is enforced before this matcher runs and also stays
    /// hard.
    /// </param>
    public static PolicyDecision Decide(AgentPolicy policy, string tool, JsonElement input,
        bool autoApprove = false)
    {
        if (string.IsNullOrWhiteSpace(tool)) return Deny("Blocked by tool policy.");
        if (string.Equals(tool, "Bash", StringComparison.Ordinal))
            return DecideCommand(policy.AllowedCommands, input, autoApprove);
        if (tool.StartsWith("mcp__", StringComparison.Ordinal))
            return Match(policy.AllowedMcpTools, tool, true)
                ? Allow() : NotCovered("MCP policy", autoApprove);
        return Match(policy.AllowedTools, tool, false)
            ? Allow() : NotCovered("tool policy", autoApprove);
    }

    private static PolicyDecision DecideCommand(IReadOnlyList<string>? allowed, JsonElement input,
        bool autoApprove)
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
        if (prefixes.Count == 0) return NotCovered("command policy", autoApprove);
        return components.All(component => prefixes.Any(prefix => IsPrefix(prefix, component)))
            ? Allow()
            : NotCovered("command policy", autoApprove);
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

    private static PolicyDecision NotCovered(string policyName, bool autoApprove) => autoApprove
        ? new("ask", $"Not covered by the session {policyName}; left to approval.")
        : new("deny", $"Blocked by {policyName}.");
}
