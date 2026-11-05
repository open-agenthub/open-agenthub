using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using AgentHub.Api.Browser;
using AgentHub.Api.Controllers;
using AgentHub.Api.Ee.Sharing;
using AgentHub.Api.Mcp;
using AgentHub.Api.Models;
using AgentHub.Api.Notifications;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using AgentHub.Api.Storage;
using k8s;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

// Persistence and access decisions are fixtures; HTTP downloads, parsing, paging, controllers
// and MCP tool methods are the production implementations. This does not prove cluster delivery.
var ct = CancellationToken.None;
var record = new SessionRecord { Id = "proof434", Owner = "owner", CallbackToken = "proof-callback-434", Status = "Running" };
var store = FixtureProxy.Make<ISessionStore>((name, _) => name switch {
    "GetByIdAsync" or "GetByCallbackTokenAsync" => Task.FromResult<SessionRecord?>(record),
    "SetQuestionPendingAsync" => Task.CompletedTask,
    _ => throw new InvalidOperationException(name)
});
var config = new ConfigurationBuilder().Build();
using var cluster = new Kubernetes(new KubernetesClientConfiguration { Host = args[1] });
var backend = new KubernetesSessionService(config, store, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!,
    NullLogger<KubernetesSessionService>.Instance, cluster: cluster);
var notifier = new RecordingNotifier();
var relay = new InternalController(store, [notifier], backend, null!, [], [], null!, null!) {
    ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
};
relay.Request.Headers["X-Agent-Token"] = record.CallbackToken;
var values = new[] { "proof-file-value-434", "proof-env-value-434", record.CallbackToken,
    "proof-header-434", "proof-custom-434", "proof-mcp-env-434", "proof-user-434", "proof-pass%2F434", "proof-pass/434", "query%2Fvalue434", "query/value434", "query+space434", "query space434" };
var result = await relay.Notify(record.Id,
    new InternalController.NotifyBody(string.Join(" ", values) + " ordinary-output", "question"), ct);
if (result is not NoContentResult) throw new Exception("Callback rejected");
foreach (var value in values)
    if (notifier.Message.Contains(value)) throw new Exception("Raw credential reached notifier");
if (!notifier.Message.EndsWith(" ordinary-output")) throw new Exception("Ordinary output changed");
Console.WriteLine("PASS real callback controller + session service + Kubernetes HTTP secrets + relay notifier boundary");

public class FixtureProxy : DispatchProxy {
    public Func<string, object?[]?, object?> Handler = null!;
    protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!.Name, args);
    public static T Make<T>(Func<string, object?[]?, object?> handler) where T : class {
        var proxy = Create<T, FixtureProxy>(); ((FixtureProxy)(object)proxy).Handler = handler; return proxy;
    }
}
sealed class LoopbackIdentity : IAgentPodIdentityResolver {
    public Task<bool> IsLiveSessionPodAsync(string id, IPAddress ip, CancellationToken ct = default) => Task.FromResult(id == "proof434" && IPAddress.IsLoopback(ip));
}
sealed class RecordingNotifier : INotifier {
    public string Message = "";
    public Task NotifyAsync(SessionRecord session, string kind, string message, CancellationToken ct = default) {
        Message = message; return Task.CompletedTask;
    }
}
