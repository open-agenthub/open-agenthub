using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using AgentHub.Api.Browser;
using AgentHub.Api.Controllers;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentHub.Api.Tests;

public class ContextClearTests
{
    private static SessionInfo Session(string owner = "alice") => new()
    {
        Id = "child", Owner = owner, Title = "proof", Phase = SessionStatus.Running,
        Agent = AgentKind.Claude, Mode = SessionMode.Interactive, UiMode = SessionUiMode.Terminal, PodIp = "127.0.0.1"
    };

    private static ISessionService Sessions(SessionInfo session)
    {
        var result = DispatchProxy.Create<ISessionService, Service>();
        ((Service)result).Session = session;
        return result;
    }

    private static ISessionStore Records()
    {
        var result = DispatchProxy.Create<ISessionStore, Store>();
        return result;
    }

    public class Service : DispatchProxy
    {
        public SessionInfo? Session;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name == "GetSessionAsync"
            ? Task.FromResult(Session) : throw new NotSupportedException(method?.Name);
    }

    public class Store : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            var id = method?.Name == "GetByCallbackTokenAsync" ? (args?[0] as string) switch
                { "parent-token" => "parent", "child-token" => "child", _ => null } : args?[0] as string;
            return Task.FromResult(id is null ? null : new SessionRecord { Id = id, Owner = "alice", CallbackToken = id + "-token" });
        }
    }

    private sealed class Clear : ISessionContextClear
    {
        public int Calls;
        public Task<ContextClearOutcome> ScheduleAsync(SessionInfo session, ContextClearRequest request, CancellationToken ct)
        { Calls++; return Task.FromResult(new ContextClearOutcome(true)); }
    }

    [Theory]
    [InlineData("child-token", true)]
    [InlineData("parent-token", false)]
    [InlineData("invalid", false)]
    public async Task CallbackTokenCanClearOnlyItsOwnSession(string token, bool allowed)
    {
        var clear = new Clear();
        var controller = new InternalController(Records(), [], Sessions(Session()), null!, [], [], null!, null!, contextClear: clear)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        controller.Request.Headers["X-Agent-Token"] = token;
        var answer = await controller.ClearContext("child", new ContextClearRequest("State and next tasks"), default);
        if (allowed) Assert.IsType<AcceptedResult>(answer.Result);
        else Assert.IsType<UnauthorizedResult>(answer.Result);
        Assert.Equal(allowed ? 1 : 0, clear.Calls);
    }

    [Theory]
    [InlineData("alice", true)]
    [InlineData("bob", false)]
    public async Task OwnerTokenMayClearButSharedAccessDoesNotGrantIt(string caller, bool allowed)
    {
        var clear = new Clear();
        var controller = new RemoteController((_, _) => Task.FromResult<RemoteCaller?>(new RemoteCaller(caller, null)),
            Sessions(Session()), contextClear: clear)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        controller.Request.Headers.Authorization = "Bearer oah_owner_context_clear_token";
        var answer = await controller.ClearContext("child", new ContextClearRequest("State and next tasks"), default);
        if (allowed) Assert.IsType<AcceptedResult>(answer.Result);
        else Assert.IsType<ForbidResult>(answer.Result);
        Assert.Equal(allowed ? 1 : 0, clear.Calls);
    }

    private sealed class Identity : IAgentPodIdentityResolver
    {
        public bool Live;
        public Task<bool> IsLiveSessionPodAsync(string id, IPAddress address, CancellationToken ct = default) => Task.FromResult(Live);
    }

    [Fact]
    public async Task RealHttpVerifiesPodIdentityBeforeSendingTheCallbackTokenAndHandover()
    {
        using var socket = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var identity = new Identity();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["AgentHub:AgentPort"] = port.ToString() }).Build();
        using var http = new HttpClient();
        var service = new SessionContextClear(TestPods.Using(http, identity), Records(), configuration);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ScheduleAsync(Session(), new ContextClearRequest("handover"), default));
        identity.Live = true;
        var receive = Task.Run(async () => {
            var context = await listener.GetContextAsync();
            Assert.Equal("/agenthub/context-clear", context.Request.Url!.AbsolutePath);
            Assert.Equal("child-token", context.Request.Headers["X-Agent-Token"]);
            using var body = new StreamReader(context.Request.InputStream);
            Assert.Contains("handover", await body.ReadToEndAsync());
            var bytes = Encoding.UTF8.GetBytes("{\"scheduled\":true}");
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        });
        Assert.True((await service.ScheduleAsync(Session(), new ContextClearRequest("handover"), default)).Scheduled);
        await receive.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
