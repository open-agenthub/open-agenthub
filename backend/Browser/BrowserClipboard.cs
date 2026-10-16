using AgentHub.Api.Ee.Sharing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace AgentHub.Api.Browser;

public sealed record BrowserClipboardPasteRequest(string? Text);
public sealed record BrowserClipboardCopyRequest(bool Cut);
public sealed record BrowserClipboardText(string Text);

// The clipboard is input to the browser, so it follows the same rule as keyboard and mouse:
// only a level that may control the browser (owner, collaborator) reaches it. Going through a
// request per action, rather than the VNC connection, is what keeps a copy from being pushed to
// every viewer of the session. Clipboard text is never logged.
public static class BrowserClipboardApi
{
    // Matches the browser supervisor's limit; checked here as well so an oversized paste is
    // rejected before it travels to the pod.
    public const int MaxTextLength = 262_144;
    public const long MaxRequestBytes = 2 * 1024 * 1024;

    public static async Task<IActionResult> PasteAsync(ControllerBase controller,
        SessionAccessResult? access, BrowserClipboardPasteRequest request,
        IBrowserService browsers, IBrowserRuntimeClient runtime, CancellationToken ct)
    {
        if (request.Text is null) return controller.BadRequest("Clipboard text is missing.");
        if (request.Text.Length > MaxTextLength)
            return controller.StatusCode(StatusCodes.Status413PayloadTooLarge);
        return await RunAsync(controller, access, browsers, ct, async podIp =>
        {
            await runtime.PasteAsync(podIp, request.Text, ct);
            return controller.NoContent();
        });
    }

    public static Task<IActionResult> CopyAsync(ControllerBase controller,
        SessionAccessResult? access, BrowserClipboardCopyRequest request,
        IBrowserService browsers, IBrowserRuntimeClient runtime, CancellationToken ct) =>
        RunAsync(controller, access, browsers, ct, async podIp =>
        {
            var text = await runtime.CopyAsync(podIp, request.Cut, ct);
            controller.Response.Headers.CacheControl = "no-store";
            return controller.Ok(new BrowserClipboardText(text));
        });

    private static async Task<IActionResult> RunAsync(ControllerBase controller,
        SessionAccessResult? access, IBrowserService browsers, CancellationToken ct,
        Func<string, Task<IActionResult>> action)
    {
        if (access is null) return controller.NotFound();
        if (!SessionAccessRules.CanWriteTerminal(access.Level))
            return controller.StatusCode(StatusCodes.Status403Forbidden);
        var connection = await browsers.GetConnectionAsync(access.Session.Id, ct);
        if (connection is null) return controller.Conflict("Browser is not running.");

        try
        {
            return await action(connection.PodIp);
        }
        catch (HttpRequestException e) when (e.StatusCode is
            System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.RequestEntityTooLarge)
        {
            return controller.StatusCode((int)e.StatusCode);
        }
        catch (HttpRequestException)
        {
            return controller.StatusCode(StatusCodes.Status502BadGateway);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return controller.StatusCode(StatusCodes.Status502BadGateway);
        }
    }
}
