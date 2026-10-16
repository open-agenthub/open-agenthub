using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentHub.Api.Services;

/// <summary>
/// Talks to an agent pod's terminal WebSocket (same endpoint the browser proxy
/// uses) to inject input, so a chat reply drives the session exactly like typing
/// in the web terminal. Also owns the control-sequence stripping the transcript
/// endpoints apply.
/// </summary>
public static class AgentTerminal
{
    public static async Task SendInputAsync(string podIp, int port, string text, CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        ws.Options.AddSubProtocol("tty");
        await ws.ConnectAsync(new Uri($"ws://{podIp}:{port}/"), ct);
        // Submit the reply as if typed. The Enter goes out as its OWN write after a
        // pause: Claude Code's TUI treats a fast multi-character burst as a paste, and
        // a "\r" inside the same chunk becomes a newline IN the input box instead of
        // submitting it — the text then sits in the prompt until someone hits Enter
        // manually. A separate, delayed write is recognized as a real keypress.
        await SendChunkAsync(ws, text, ct);
        await Task.Delay(300, ct);
        await SendChunkAsync(ws, "\r", ct);
        await Task.Delay(150, ct);
        try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); } catch { }
    }

    private static Task SendChunkAsync(ClientWebSocket ws, string data, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new { type = "input", data });
        return ws.SendAsync(Encoding.UTF8.GetBytes(payload), WebSocketMessageType.Text, true, ct);
    }

    // ANSI/terminal escape stripping. ESC (0x1b) and BEL (0x07) are built from char
    // codes so the source contains only plain ASCII (no hidden control bytes).
    private static readonly string Esc = ((char)27).ToString();
    private static readonly string Bel = ((char)7).ToString();
    private static readonly Regex Osc = new("(?:" + Esc + "\\]|\u009d).*?(?:" + Bel + "|\u009c|" + Esc + "\\\\)", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex Csi = new("(?:" + Esc + "\\[|\u009b)[0-?]*[ -/]*[@-~]", RegexOptions.Compiled);
    private static readonly Regex OtherEsc = new(Esc + "(?:[78]|[ -/]*[@-~])", RegexOptions.Compiled);
    private static readonly Regex C1 = new("[\u0080-\u009f]", RegexOptions.Compiled);

    /// <summary>Strips terminal escape sequences and carriage returns for readable plain text.</summary>
    public static string StripAnsi(string s)
    {
        s = Osc.Replace(s, "");
        s = Csi.Replace(s, "");
        s = OtherEsc.Replace(s, "");
        s = C1.Replace(s, "");
        return s.Replace("\r\n", "\n").Replace('\r', '\n');
    }

    /// <summary>Preserves a missing transcript and converts stored terminal output to plain text.</summary>
    public static string? CleanTranscript(string? s) => s is null ? null : StripAnsi(s);
}
