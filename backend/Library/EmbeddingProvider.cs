using System.Text.Json;

namespace AgentHub.Api.Library;

/// <summary>
/// Turns text into an embedding vector for semantic skill search. Disabled by
/// default; full-text search always works without it.
/// </summary>
public interface IEmbeddingProvider
{
    /// <summary>False when no provider is configured — callers skip vector search.</summary>
    bool Enabled { get; }
    /// <summary>Model identifier, stored next to vectors so stale ones can be detected.</summary>
    string Model { get; }
    /// <summary>Embeds one text; null on provider errors (callers degrade to FTS).</summary>
    Task<float[]?> EmbedAsync(string text, CancellationToken ct = default);
}

public sealed class NullEmbeddingProvider : IEmbeddingProvider
{
    public bool Enabled => false;
    public string Model => "";
    public Task<float[]?> EmbedAsync(string text, CancellationToken ct = default) =>
        Task.FromResult<float[]?>(null);
}

/// <summary>
/// Calls an OpenAI-compatible <c>/embeddings</c> endpoint (OpenAI, Voyage,
/// Ollama, LocalAI, …). Configured via
/// <c>Embeddings:Endpoint</c> (base URL), <c>Embeddings:Model</c> and the
/// optional <c>Embeddings:ApiKey</c>.
/// </summary>
public sealed class OpenAiCompatibleEmbeddingProvider : IEmbeddingProvider
{
    /// <summary>Inputs are capped so oversized skills cannot blow the request.</summary>
    public const int MaxInputChars = 8_000;

    private readonly IHttpClientFactory _http;
    private readonly ILogger<OpenAiCompatibleEmbeddingProvider> _log;
    private readonly string _endpoint;
    private readonly string? _apiKey;

    public OpenAiCompatibleEmbeddingProvider(
        IConfiguration cfg, IHttpClientFactory http, ILogger<OpenAiCompatibleEmbeddingProvider> log)
    {
        _http = http;
        _log = log;
        _endpoint = (cfg["Embeddings:Endpoint"] ?? "").TrimEnd('/');
        _apiKey = cfg["Embeddings:ApiKey"];
        Model = cfg["Embeddings:Model"] ?? "";
    }

    public bool Enabled => _endpoint.Length > 0 && Model.Length > 0;
    public string Model { get; }

    public async Task<float[]?> EmbedAsync(string text, CancellationToken ct = default)
    {
        if (!Enabled) return null;
        var input = text.Length > MaxInputChars ? text[..MaxInputChars] : text;
        try
        {
            using var client = _http.CreateClient("embeddings");
            client.Timeout = TimeSpan.FromSeconds(20);
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_endpoint}/embeddings")
            {
                Content = JsonContent.Create(new { model = Model, input })
            };
            if (!string.IsNullOrEmpty(_apiKey))
                request.Headers.Authorization = new("Bearer", _apiKey);
            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                _log.LogWarning("Embedding request failed with {Status}.", (int)response.StatusCode);
                return null;
            }
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var vector = doc.RootElement.GetProperty("data")[0].GetProperty("embedding");
            var result = new float[vector.GetArrayLength()];
            var i = 0;
            foreach (var item in vector.EnumerateArray()) result[i++] = item.GetSingle();
            return result;
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.LogWarning(e, "Embedding request failed.");
            return null;
        }
    }
}
