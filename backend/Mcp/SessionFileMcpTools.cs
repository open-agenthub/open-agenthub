using System.ComponentModel;
using System.Text;
using AgentHub.Api.Ee.Sharing;
using AgentHub.Api.Files;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AgentHub.Api.Mcp;

[McpServerToolType]
public sealed class SessionFileMcpTools(
    IHttpContextAccessor http, ISessionAccessService access, ISessionFileService files)
{
    [McpServerTool(Name = "list_files")]
    [Description("List ready files in an accessible AgentHub session. Use read_file to retrieve an image or text file.")]
    public async Task<IReadOnlyList<SessionFileResponse>> ListFiles(string sessionId, CancellationToken ct = default)
    {
        var actor = await ActorAsync(sessionId, ct);
        return (await files.ListAsync(actor, ct)).Where(file => file.State == SessionFileState.Ready)
            .Select(SessionFileApi.Response).ToArray();
    }

    [McpServerTool(Name = "read_file")]
    [Description("Read an image (up to 20 MiB) or text file (up to 64 KiB) from an accessible AgentHub session. Returns image pixels or text, not an internal storage URL. Use the stdio MCP for downloading other or larger files to your computer.")]
    public async Task<CallToolResult> ReadFile(string sessionId, string fileId, CancellationToken ct = default)
    {
        var actor = await ActorAsync(sessionId, ct);
        try
        {
            var file = (await files.ListAsync(actor, ct)).FirstOrDefault(file =>
                file.Id == fileId && file.State == SessionFileState.Ready);
            if (file is null) throw new McpException("file_not_found");
            var mime = file.DetectedMimeType ?? file.DeclaredMimeType;
            var image = mime is "image/png" or "image/jpeg" or "image/webp" or "image/gif";
            if (!image && mime is not ("text/plain" or "text/markdown"))
                throw new McpException("file_type_not_supported_use_stdio");
            var limit = image ? 20 * 1024 * 1024 : 64 * 1024;
            if (file.Size > limit) throw new McpException("file_too_large_use_stdio");
            var content = await files.OpenContentAsync(actor, fileId, allowRedirect: false, ct: ct);
            await using var stream = content.Content ?? throw new McpException("file_not_found");
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, ct)) != 0)
            {
                if (buffer.Length + read > limit) throw new McpException("file_too_large_use_stdio");
                await buffer.WriteAsync(chunk.AsMemory(0, read), ct);
            }
            if (buffer.Length != file.Size) throw new McpException("file_size_mismatch");
            return new CallToolResult
            {
                Content = [image ? ImageContentBlock.FromBytes(buffer.ToArray(), mime)
                    : new TextContentBlock { Text = Encoding.UTF8.GetString(buffer.ToArray()) }]
            };
        }
        catch (SessionFileException exception) { throw new McpException(exception.Code); }
    }

    private async Task<SessionFileActor> ActorAsync(string id, CancellationToken ct)
    {
        var principal = McpCallerIdentity.Owner(http);
        var resolved = await access.ResolveUserAsync(principal, id, ct);
        if (resolved is null || !SessionAccessRules.CanReadFiles(resolved.Level))
            throw new McpException("session_not_found");
        return SessionFileApi.Actor(resolved, principal);
    }
}
