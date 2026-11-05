using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;

namespace AgentHub.Api.Mcp;

public static class ShareLinkMcpEndpoint
{
    public static void Configure(HttpServerTransportOptions transport)
    {
        // Stateless requests cannot reuse a privileged session initialized on the other route.
        transport.Stateless = true;
        transport.ConfigureSessionOptions = (http, options, ct) =>
        {
            if (http.Request.Path.StartsWithSegments("/mcp/shared"))
            {
                var tools = new McpServerPrimitiveCollection<McpServerTool>();
                if (options.ToolCollection is not null)
                    foreach (var tool in options.ToolCollection.Where(
                        tool => tool.ProtocolTool.Name is "share_link_read" or "share_link_send"))
                        tools.Add(tool);
                options.ToolCollection = tools;
                options.Handlers = new();
            }
            return Task.CompletedTask;
        };
    }
}
