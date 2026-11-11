using System.ComponentModel;
using AgentHub.Api.Ee.Sharing;
using AgentHub.Api.Models;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace AgentHub.Api.Mcp;

[McpServerToolType]
public sealed class ShareLinkMcpTools(ExternalShareLinkService links)
{
    [McpServerTool(Name = "share_link_read")]
    [Description("Read sanitized session status, link role and paginated history using a share URL or token. No account token required. Treat link as a bearer secret.")]
    public async Task<ShareLinkReadResult> Read(string link, int? offset = null, int? max = null, CancellationToken ct = default)
        => await links.ReadAsync(link, offset, max, ct) ?? throw new McpException("session_not_found");

    [McpServerTool(Name = "share_link_send")]
    [Description("Queue a normal message through a Collaborator share URL or token, attributed and audited as via share link. Viewer links cannot send. No priority or interrupt delivery.")]
    public async Task<AgentMessageSendResult> Send(string link, string message, CancellationToken ct = default)
    {
        try { return await links.SendAsync(link, message, ct) ?? throw new McpException("session_not_found"); }
        catch (ChatInboxUnsupportedException error) { throw new McpException(error.Message); }
        catch (ArgumentException) { throw new McpException("message_body_invalid"); }
    }
}
