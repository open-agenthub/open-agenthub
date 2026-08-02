using System.Net;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;

namespace AgentHub.Api.Files;

public interface IAgentFileClient
{
    Task PutAsync(
        SessionInfo session,
        SessionFileRecord file,
        Stream content,
        CancellationToken ct);
    Task<Stream?> OpenReadAsync(
        SessionInfo session,
        SessionFileRecord file,
        CancellationToken ct);
    Task<bool> ExistsAsync(
        SessionInfo session,
        SessionFileRecord file,
        CancellationToken ct);
    Task DeleteAsync(
        SessionInfo session,
        SessionFileRecord file,
        CancellationToken ct);
}

public sealed class AgentFileClient : IAgentFileClient
{
    private readonly HttpClient _http;
    private readonly ISessionStore _sessions;
    private readonly int _agentPort;

    public AgentFileClient(HttpClient http, ISessionStore sessions, IConfiguration configuration)
    {
        _http = http;
        _sessions = sessions;
        _agentPort = configuration.GetValue("AgentHub:AgentPort", 7681);
    }

    public async Task PutAsync(
        SessionInfo session,
        SessionFileRecord file,
        Stream content,
        CancellationToken ct)
    {
        using var request = await CreateRequestAsync(HttpMethod.Put, session, file, ct);
        request.Content = new StreamContent(content);
        request.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue(file.DeclaredMimeType);
        using var response = await _http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task<Stream?> OpenReadAsync(
        SessionInfo session,
        SessionFileRecord file,
        CancellationToken ct)
    {
        using var request = await CreateRequestAsync(HttpMethod.Get, session, file, ct);
        var response = await _http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            response.Dispose();
            return null;
        }

        try
        {
            response.EnsureSuccessStatusCode();
            return new ResponseOwnedStream(
                await response.Content.ReadAsStreamAsync(ct), response, file.Size);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    public async Task<bool> ExistsAsync(
        SessionInfo session,
        SessionFileRecord file,
        CancellationToken ct)
    {
        using var request = await CreateRequestAsync(HttpMethod.Head, session, file, ct);
        using var response = await _http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
        return true;
    }

    public async Task DeleteAsync(
        SessionInfo session,
        SessionFileRecord file,
        CancellationToken ct)
    {
        using var request = await CreateRequestAsync(HttpMethod.Delete, session, file, ct);
        using var response = await _http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            response.EnsureSuccessStatusCode();
        }
    }

    private async Task<HttpRequestMessage> CreateRequestAsync(
        HttpMethod method,
        SessionInfo session,
        SessionFileRecord file,
        CancellationToken ct)
    {
        if (session.Phase != "Running" || string.IsNullOrWhiteSpace(session.PodIp))
        {
            throw new InvalidOperationException("The session pod is not running.");
        }

        var stored = await _sessions.GetAsync(session.Owner, session.Id, ct)
            ?? throw new InvalidOperationException("The session record is unavailable.");
        var uri = new UriBuilder("http", session.PodIp, _agentPort,
            $"agenthub/files/{Uri.EscapeDataString(file.Id)}").Uri;
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Add("X-Agent-Token", stored.CallbackToken);
        return request;
    }

    private sealed class ResponseOwnedStream(
        Stream inner,
        HttpResponseMessage response,
        long maximumBytes) : Stream
    {
        private long _read;
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => maximumBytes;
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            Count(read);
            return read;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var read = await inner.ReadAsync(buffer, ct);
            Count(read);
            return read;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                response.Dispose();
            }
            base.Dispose(disposing);
        }
        public override ValueTask DisposeAsync()
        {
            response.Dispose();
            GC.SuppressFinalize(this);
            return ValueTask.CompletedTask;
        }

        private void Count(int read)
        {
            _read += read;
            if (_read > maximumBytes)
            {
                throw new InvalidDataException(
                    "The agent returned more bytes than registered.");
            }
        }
    }
}
