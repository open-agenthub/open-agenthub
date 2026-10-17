using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace AgentHub.Api.Services;

/// <summary>
/// Replaces the values of a session's secrets in text that leaves the pod for someone else to read
/// (docs/session-secrets.md). The session agent applies the same rules to what it uploads
/// (agent-runtime/common/secret-mask.js); this copy covers the hook messages the chat relays post.
///
/// A value shorter than <see cref="MinLength"/> is not masked: "1234" or "true" would be replaced
/// in every number and every sentence, which mangles the text and still gives the value away by
/// where the gaps are.
/// </summary>
public static class SecretMask
{
    public const int MinLength = 8;

    // What node's JSON.stringify produces, which is what the CLIs write their transcripts with: the
    // default encoder would also escape "+", "<" or any non-ASCII letter and miss those values.
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Text with every occurrence of a value, its JSON-escaped form, and each long enough
    /// line of a multi-line value replaced by <c>[secret:NAME]</c>.</summary>
    public static string Apply(string text, IEnumerable<KeyValuePair<string, byte[]>> secrets)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var patterns = new List<(string Needle, string Name)>();
        foreach (var (name, raw) in secrets)
        {
            var value = Encoding.UTF8.GetString(raw);
            foreach (var needle in Needles(value)) patterns.Add((needle, name));
        }
        // Longest first, so a value that contains another one is replaced whole rather than
        // leaving its remainder in the text.
        foreach (var (needle, name) in patterns.DistinctBy(p => p.Needle).OrderByDescending(p => p.Needle.Length))
            text = text.Replace(needle, $"[secret:{name}]", StringComparison.Ordinal);
        return text;
    }

    private static IEnumerable<string> Needles(string value)
    {
        var candidates = new List<string> { value, value.TrimEnd('\r', '\n') };
        // A transcript is JSON lines: a value with a quote or a newline appears escaped there.
        candidates.Add(JsonSerializer.Serialize(value, Json)[1..^1]);
        // A terminal turns \n into \r\n, and an agent often prints part of a key file.
        candidates.AddRange(value.Split('\n').Select(line => line.TrimEnd('\r')));
        return candidates.Where(c => c.Trim().Length >= MinLength);
    }
}
