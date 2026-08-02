using System.Net;
using AgentHub.Api.Files;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentHub.Api.Tests;

public sealed class AgentFileClientTests
{
    [Fact]
    public async Task Put_streams_to_the_managed_path_with_only_the_agent_token()
    {
        HttpRequestMessage? captured = null;
        byte[]? body = null;
        var client = Client(async request =>
        {
            captured = request;
            body = await request.Content!.ReadAsByteArrayAsync();
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        await using var content = new MemoryStream(PngBytes());

        await client.PutAsync(Session(), File(), content, default);

        Assert.Equal(HttpMethod.Put, captured!.Method);
        Assert.Equal("http://10.0.0.8:7681/agenthub/files/f1", captured.RequestUri!.ToString());
        Assert.Equal("secret-token", captured.Headers.GetValues("X-Agent-Token").Single());
        Assert.Equal(PngBytes(), body);
    }

    [Fact]
    public async Task OpenRead_throws_when_the_agent_returns_more_than_the_registered_size()
    {
        var client = Client(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[9]),
        }));
        await using var stream = await client.OpenReadAsync(Session(), File(), default);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            using var sink = new MemoryStream();
            await stream!.CopyToAsync(sink);
        });
    }

    [Fact]
    public async Task Exists_uses_head_and_maps_not_found_to_false()
    {
        HttpMethod? method = null;
        var client = Client(request =>
        {
            method = request.Method;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        Assert.False(await client.ExistsAsync(Session(), File(), default));
        Assert.Equal(HttpMethod.Head, method);
    }

    [Fact]
    public async Task Delete_treats_an_already_missing_temporary_file_as_success()
    {
        var client = Client(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.NotFound)));

        await client.DeleteAsync(Session(), File(), default);
    }

    private static AgentFileClient Client(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> send)
    {
        var http = new HttpClient(new DelegateHandler(send));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AgentHub:AgentPort"] = "7681",
            })
            .Build();
        return new AgentFileClient(http, new TokenSessionStore(), configuration);
    }

    private static SessionInfo Session() => new()
    {
        Id = "s1",
        Owner = "alice",
        Title = "session",
        Mode = SessionMode.Interactive,
        Phase = "Running",
        PodIp = "10.0.0.8",
    };

    private static SessionFileRecord File() => new(
        "f1", "s1", "alice", "shot.png", ".png", "image/png", null, 8,
        SessionFileStorageKind.Pod, "f1/shot.png", SessionFileState.Uploading,
        SessionFilePreviewState.None, null, "alice", "user", DateTime.UtcNow,
        null, null);

    private static byte[] PngBytes() =>
        [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request);
    }

    private sealed class TokenSessionStore : ISessionStore
    {
        public Task<SessionRecord?> GetAsync(string owner, string id, CancellationToken ct = default) =>
            Task.FromResult<SessionRecord?>(new SessionRecord
            {
                Id = id,
                Owner = owner,
                CallbackToken = "secret-token",
            });
        public Task InitializeAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpsertAsync(SessionRecord r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionRecord?> GetByCallbackTokenAsync(string token, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SessionRecord>> ListAsync(string owner, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateStatusAsync(string id, string status, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetQuestionPendingAsync(string id, bool pending, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetScrollbackAsync(string id, string text, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> GetScrollbackAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
