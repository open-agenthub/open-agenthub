// -----------------------------------------------------------------------------
// Open AgentHub Enterprise Edition — Slack integration.
// Part of the Enterprise Edition; NOT covered by the AGPL-3.0 license of the
// open-core. Source-available under the Open AgentHub Enterprise License
// (see ee/LICENSE); a valid subscription is required for production use.
// -----------------------------------------------------------------------------
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentHub.Api.Ee.Slack;

/// <summary>
/// Talks to an agent pod's terminal WebSocket (same endpoint the browser proxy
/// uses): reads the current scrollback and injects input, so a Slack reply drives
/// the session exactly like typing in the web terminal.
/// </summary>
public static class AgentTerminal
{
    public static async Task<string> ReadScrollbackAsync(string podIp, int port, CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        ws.Options.AddSubProtocol("tty");
        await ws.ConnectAsync(new Uri($"ws://{podIp}:{port}/"), ct);

        var sb = new StringBuilder();
        var buf = new byte[16 * 1024];
        // The agent sends the whole scrollback on connect; read until a short idle gap.
        while (true)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            idle.CancelAfter(TimeSpan.FromMilliseconds(500));
            try
            {
                var msg = await ws.ReceiveAsync(buf, idle.Token);
                if (msg.MessageType == WebSocketMessageType.Close) break;
                sb.Append(Encoding.UTF8.GetString(buf, 0, msg.Count));
                if (sb.Length > 500_000) break;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { break; } // idle → done
        }
        try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); } catch { }
        return sb.ToString();
    }

    public static async Task SendInputAsync(string podIp, int port, string text, CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        ws.Options.AddSubProtocol("tty");
        await ws.ConnectAsync(new Uri($"ws://{podIp}:{port}/"), ct);
        // Submit the reply as if typed, followed by Enter.
        var payload = JsonSerializer.Serialize(new { type = "input", data = text + "\r" });
        await ws.SendAsync(Encoding.UTF8.GetBytes(payload), WebSocketMessageType.Text, true, ct);
        await Task.Delay(150, ct);
        try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); } catch { }
    }

    // ANSI/terminal escape stripping. ESC (0x1b) and BEL (0x07) are built from char
    // codes so the source contains only plain ASCII (no hidden control bytes).
    private static readonly string Esc = ((char)27).ToString();
    private static readonly string Bel = ((char)7).ToString();
    private static readonly Regex Osc = new(Esc + "\\].*?(?:" + Bel + "|" + Esc + "\\\\)", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex Csi = new(Esc + "\\[[0-9;?]*[ -/]*[@-~]", RegexOptions.Compiled);
    private static readonly Regex OtherEsc = new(Esc + "[@-Z\\\\-_]", RegexOptions.Compiled);

    /// <summary>Strips ANSI escape sequences and carriage returns for readable Slack text.</summary>
    public static string StripAnsi(string s)
    {
        s = Osc.Replace(s, "");
        s = Csi.Replace(s, "");
        s = OtherEsc.Replace(s, "");
        return s.Replace("\r\n", "\n").Replace('\r', '\n');
    }
}
