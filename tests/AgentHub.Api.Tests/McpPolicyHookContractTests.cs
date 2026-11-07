using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using AgentHub.Api.Controllers;
using AgentHub.Api.Ee.Sharing;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>
/// Runs the Claude runtime's real mcp-policy-hook.sh against the real InternalController.McpPolicy
/// served over HTTP with the backend's JSON options. The hook's own tests answer from a fake that
/// sent the fields the hook expected, while the controller sent fewer — so every blocked tool fell
/// through to the approval. Only a test that puts both real halves together catches that.
/// </summary>
public sealed class McpPolicyHookContractTests : IDisposable
{
    private const string Token = "callback-token";
    private const string ApprovalMarker = "approval-stub";
    private readonly string _scratch = Path.Join(Path.GetTempPath(), "mcp-hook-contract-" + Guid.NewGuid().ToString("N"));
    private SessionMcpPolicy? _policy;

    public McpPolicyHookContractTests() => Directory.CreateDirectory(_scratch);

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task BlockedTool_IsDeniedWithoutReachingTheApproval()
    {
        if (OperatingSystem.IsWindows()) return;
        _policy = new SessionMcpPolicy(["docs"], [], DateTime.UtcNow);
        await using var backend = await StartBackendAsync();

        var result = await RunHookAsync(backend.Url, "mcp__docs__search");

        Assert.Equal("deny", result.Decision);
        Assert.Contains("MCP sharing policy", result.Reason);
        Assert.Equal("1", RememberedRestriction());
    }

    [Fact]
    public async Task AllowedToolInARestrictedSession_GoesToTheApproval()
    {
        if (OperatingSystem.IsWindows()) return;
        _policy = new SessionMcpPolicy([], ["mcp__docs__delete"], DateTime.UtcNow);
        await using var backend = await StartBackendAsync();

        var result = await RunHookAsync(backend.Url, "mcp__docs__search");

        Assert.Equal(ApprovalMarker, result.Reason);
        Assert.Equal("1", RememberedRestriction());
    }

    [Fact]
    public async Task UnrestrictedSession_GoesToTheApproval()
    {
        if (OperatingSystem.IsWindows()) return;
        _policy = null;
        await using var backend = await StartBackendAsync();

        var result = await RunHookAsync(backend.Url, "mcp__docs__search");

        Assert.Equal(ApprovalMarker, result.Reason);
        Assert.Equal("0", RememberedRestriction());
    }

    [Fact]
    public async Task Unreachable_PodStartedRestricted_Denies()
    {
        if (OperatingSystem.IsWindows()) return;
        var url = await UnreachableUrlAsync();

        var result = await RunHookAsync(url, "mcp__docs__search", podStartedRestricted: true);

        Assert.Equal("deny", result.Decision);
    }

    [Fact]
    public async Task Unreachable_RestrictionAddedWhileRunning_Denies()
    {
        if (OperatingSystem.IsWindows()) return;
        // The pod started unrestricted; the owner blocked a server afterwards, the hook saw that on
        // one call, and the backend then became unreachable.
        _policy = new SessionMcpPolicy(["docs"], [], DateTime.UtcNow);
        string url;
        await using (var backend = await StartBackendAsync())
        {
            url = backend.Url;
            Assert.Equal(ApprovalMarker, (await RunHookAsync(url, "mcp__other__read")).Reason);
        }

        var result = await RunHookAsync(url, "mcp__other__read");

        Assert.Equal("deny", result.Decision);
    }

    [Fact]
    public async Task Unreachable_UnrestrictedSession_GoesToTheApproval()
    {
        if (OperatingSystem.IsWindows()) return;
        var url = await UnreachableUrlAsync();

        var result = await RunHookAsync(url, "mcp__docs__search");

        Assert.Equal(ApprovalMarker, result.Reason);
    }

    [Fact]
    public async Task Controller_ReturnsTheFieldsTheHookReads()
    {
        _policy = new SessionMcpPolicy(["docs"], [], DateTime.UtcNow);
        await using var backend = await StartBackendAsync();
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Add("X-Agent-Token", Token);

        using var response = await http.PostAsJsonAsync($"{backend.Url}/mcp-policy", new { tool = "mcp__docs__search" });
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.True(json.RootElement.GetProperty("restricted").GetBoolean());
        Assert.Equal("deny", json.RootElement.GetProperty("decision").GetString());
    }

    private sealed record HookResult(string Decision, string Reason);

    private sealed class Backend(WebApplication app, string url) : IAsyncDisposable
    {
        public string Url { get; } = url;
        public async ValueTask DisposeAsync() => await app.DisposeAsync();
    }

    private async Task<Backend> StartBackendAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        // The backend's own MVC setup (Program.cs), so the response is serialised as in production.
        builder.Services.AddControllers().AddJsonOptions(o =>
            o.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
        var app = builder.Build();
        var session = new SessionRecord
        {
            Id = "session-1", Owner = "alice", CallbackToken = Token,
            Mode = SessionMode.Interactive, Agent = AgentKind.Claude, AuthMode = AgentAuthMode.ApiKey
        };
        app.MapPost("/internal/sessions/{id}/mcp-policy", async (HttpContext context, string id) =>
        {
            var body = await context.Request.ReadFromJsonAsync<InternalController.PermissionBody>(context.RequestAborted);
            var controller = new InternalController(
                new InternalAgentPolicyTests.CallbackSessionStore(session), [], null!, null!, [], [], new PolicyReader(() => _policy), null!)
            {
                ControllerContext = new ControllerContext { HttpContext = context }
            };
            var result = await controller.McpPolicy(id, body!, context.RequestAborted);
            await result.ExecuteResultAsync(new ActionContext(context, context.GetRouteData(), new ActionDescriptor()));
        });
        await app.StartAsync();
        return new Backend(app, app.Urls.Single() + "/internal/sessions/session-1");
    }

    private async Task<string> UnreachableUrlAsync()
    {
        string url;
        await using (var backend = await StartBackendAsync()) url = backend.Url;
        return url;
    }

    private string RememberedRestriction()
    {
        var path = Path.Join(_scratch, "agenthub-mcp-policy-restricted");
        return File.Exists(path) ? File.ReadAllText(path) : "";
    }

    private async Task<HookResult> RunHookAsync(string callbackUrl, string tool, bool podStartedRestricted = false)
    {
        var approval = Path.Join(_scratch, "approval-hook.sh");
        if (!File.Exists(approval))
        {
            await File.WriteAllTextAsync(approval,
                "#!/usr/bin/env bash\ncat >/dev/null\nprintf '%s\\n' '{\"hookSpecificOutput\":{\"hookEventName\":\"PreToolUse\",\"permissionDecision\":\"ask\",\"permissionDecisionReason\":\"" + ApprovalMarker + "\"}}'\n");
            File.SetUnixFileMode(approval, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var start = new ProcessStartInfo("bash")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(Path.Join(RepoRoot(), "agent-runtime", "claude", "hooks", "mcp-policy-hook.sh"));
        start.Environment["AGENTHUB_CALLBACK_URL"] = callbackUrl;
        start.Environment["AGENTHUB_CALLBACK_TOKEN"] = Token;
        start.Environment["AGENTHUB_APPROVAL_HOOK"] = approval;
        start.Environment["AGENTHUB_MCP_POLICY"] = podStartedRestricted ? "1" : "0";
        start.Environment["TMPDIR"] = _scratch;

        using var process = Process.Start(start)!;
        await process.StandardInput.WriteAsync(JsonSerializer.Serialize(new
        {
            hook_event_name = "PreToolUse", tool_name = tool, tool_input = new { }
        }));
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await process.WaitForExitAsync(timeout.Token);
        Assert.True(process.ExitCode == 0, await error);

        using var json = JsonDocument.Parse(await output);
        var hook = json.RootElement.GetProperty("hookSpecificOutput");
        return new HookResult(
            hook.GetProperty("permissionDecision").GetString()!,
            hook.GetProperty("permissionDecisionReason").GetString()!);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Join(dir.FullName, "agent-runtime", "claude", "hooks", "mcp-policy-hook.sh")))
                return dir.FullName;
        throw new InvalidOperationException("Repository root not found above " + AppContext.BaseDirectory);
    }

    private sealed class PolicyReader(Func<SessionMcpPolicy?> policy) : ISessionMcpPolicyReader
    {
        public Task<SessionMcpPolicy?> GetMcpPolicyAsync(string sessionId, CancellationToken ct = default) =>
            Task.FromResult(policy());
    }
}
