using Amazon.S3;
using Amazon.S3.Model;
using AgentHub.Api.Models;

namespace AgentHub.Api.Storage;

public sealed record ArtifactObjectInfo(long Size, string? ContentType);

/// <summary>
/// Storage in S3 (or MinIO). The agent pod never receives S3 credentials,
/// only time-limited presigned URLs.
/// Key layout: sessions/{owner}/{sessionId}/{state.tgz|scrollback.log|artifacts/...}
/// </summary>
public interface IArtifactStore
{
    bool IsConfigured => true;
    /// <summary>
    /// Whether a browser can reach object storage directly, i.e. a presigned URL is worth
    /// handing out. False for the common setup where the endpoint is a cluster-internal
    /// address — the caller has to proxy the bytes through the API instead.
    /// </summary>
    bool CanServeBrowsersDirectly => false;
    string PresignPut(string key, TimeSpan ttl);
    string PresignGet(string key, TimeSpan ttl);
    /// <summary>Streams content in. Returns false when no object storage is configured.</summary>
    Task<bool> TryPutStreamAsync(string key, Stream content, string? contentType, CancellationToken ct = default)
        => Task.FromResult(false);
    Task<string?> GetTextAsync(string key, CancellationToken ct = default);
    /// <summary>Writes text content. Returns false when no object storage is configured
    /// (NullArtifactStore) so callers can fall back to database storage.</summary>
    Task<bool> TryPutTextAsync(string key, string text, CancellationToken ct = default) => Task.FromResult(false);
    /// <summary>Best-effort delete; missing objects are not an error.</summary>
    Task DeleteAsync(string key, CancellationToken ct = default);
    Task<ArtifactObjectInfo?> HeadAsync(string key, CancellationToken ct = default) => Task.FromResult<ArtifactObjectInfo?>(null);
    Task<Stream?> OpenReadAsync(string key, CancellationToken ct = default) => Task.FromResult<Stream?>(null);

    static string StateKey(string owner, string id) => StateKey(owner, id, AgentKind.Claude);
    static string StateKey(string owner, string id, AgentKind agent) =>
        $"sessions/{owner}/{id}/{agent switch
        {
            AgentKind.Claude => "claude-state.tgz",
            AgentKind.Codex => "codex-state.tgz",
            AgentKind.Cursor => "cursor-state.tgz",
            AgentKind.OpenClaw => "openclaw-state.tgz",
            _ => throw new ArgumentOutOfRangeException(nameof(agent), agent, "Unknown agent kind.")
        }}";
    static string ScrollbackKey(string owner, string id) => $"sessions/{owner}/{id}/scrollback.log";
    static string BrowserCookiesKey(string owner, string id) =>
        $"sessions/{owner}/{id}/browser-cookies.json";
    static string ArtifactKey(string owner, string id, string name)
        => $"sessions/{owner}/{id}/artifacts/{name.TrimStart('/')}";
    /// <summary>Head SKILL.md content of a library skill (see SkillStore).</summary>
    static string SkillKey(string id) => $"skills/{id}/SKILL.md";
    /// <summary>Immutable per-version SKILL.md copy (see SkillStore).</summary>
    static string SkillVersionKey(string id, int version) => $"skills/{id}/v{version}/SKILL.md";
    /// <summary>Extra skill file (script, template) of one version (see SkillStore).</summary>
    static string SkillFileKey(string id, int version, string path) => $"skills/{id}/v{version}/files/{path}";
    static string SessionFileKey(string owner, string id, string fileId, string name) =>
        $"sessions/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(id)}/files/" +
        $"{Uri.EscapeDataString(fileId)}/{Uri.EscapeDataString(name)}";
}

/// <summary>
/// Fallback when S3 is not configured: no persistence of state/scrollback/artifacts.
/// Sessions run normally, but resume and the transcript view are disabled.
/// </summary>
public sealed class NullArtifactStore : IArtifactStore
{
    public string PresignPut(string key, TimeSpan ttl) => "";
    public string PresignGet(string key, TimeSpan ttl) => "";
    public bool IsConfigured => false;
    public Task<string?> GetTextAsync(string key, CancellationToken ct = default) => Task.FromResult<string?>(null);
    public Task<bool> TryPutTextAsync(string key, string text, CancellationToken ct = default) => Task.FromResult(false);
    public Task DeleteAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
    public Task<ArtifactObjectInfo?> HeadAsync(string key, CancellationToken ct = default) => Task.FromResult<ArtifactObjectInfo?>(null);
    public Task<Stream?> OpenReadAsync(string key, CancellationToken ct = default) => Task.FromResult<Stream?>(null);
}

public sealed class S3ArtifactStore : IArtifactStore
{
    private readonly IAmazonS3 _s3;
    // Signs URLs against the externally reachable endpoint. Null when none is configured,
    // which is what makes CanServeBrowsersDirectly false.
    private readonly IAmazonS3? _publicS3;
    private readonly string _bucket;

    public bool IsConfigured => true;
    public bool CanServeBrowsersDirectly => _publicS3 is not null;

    public S3ArtifactStore(IConfiguration cfg)
    {
        var s = cfg.GetSection("S3");
        _bucket = s["Bucket"] ?? throw new InvalidOperationException("S3:Bucket is missing.");

        var s3cfg = new AmazonS3Config { ForcePathStyle = true }; // MinIO prefers path-style
        if (!string.IsNullOrEmpty(s["ServiceUrl"])) s3cfg.ServiceURL = s["ServiceUrl"];
        if (!string.IsNullOrEmpty(s["Region"])) s3cfg.AuthenticationRegion = s["Region"];
        // Internal MinIO endpoints often use a self-signed certificate. Opt-in only.
        if (s.GetValue("InsecureTls", false)) s3cfg.HttpClientFactory = new InsecureHttpClientFactory();

        _s3 = new AmazonS3Client(s["AccessKey"], s["SecretKey"], s3cfg);

        // ServiceUrl is where the backend talks to storage, which is usually a Service
        // address no browser can resolve. Only a separately configured public endpoint
        // makes presigned URLs usable by a client.
        var publicUrl = s["PublicUrl"];
        if (!string.IsNullOrWhiteSpace(publicUrl))
        {
            var publicCfg = new AmazonS3Config { ForcePathStyle = true, ServiceURL = publicUrl.Trim() };
            if (!string.IsNullOrEmpty(s["Region"])) publicCfg.AuthenticationRegion = s["Region"];
            _publicS3 = new AmazonS3Client(s["AccessKey"], s["SecretKey"], publicCfg);
        }
    }

    /// <summary>Produces HttpClients that skip TLS server-certificate validation
    /// (for internal S3/MinIO endpoints with a self-signed certificate). Opt-in.</summary>
    private sealed class InsecureHttpClientFactory : Amazon.Runtime.HttpClientFactory
    {
        public override HttpClient CreateHttpClient(Amazon.Runtime.IClientConfig config) =>
            new(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true });
        public override string GetConfigUniqueString(Amazon.Runtime.IClientConfig config) => "insecure-tls";
    }

    public string PresignPut(string key, TimeSpan ttl) => Presign(key, HttpVerb.PUT, ttl);
    public string PresignGet(string key, TimeSpan ttl) => Presign(key, HttpVerb.GET, ttl);

    private string Presign(string key, HttpVerb verb, TimeSpan ttl) =>
        (_publicS3 ?? _s3).GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = _bucket,
            Key = key,
            Verb = verb,
            Expires = DateTime.UtcNow.Add(ttl)
        });

    public async Task<ArtifactObjectInfo?> HeadAsync(string key, CancellationToken ct = default)
    {
        try
        {
            var response = await _s3.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = _bucket,
                Key = key,
            }, ct);
            return new ArtifactObjectInfo(response.ContentLength, response.Headers.ContentType);
        }
        catch (AmazonS3Exception exception) when (IsNotFound(exception))
        {
            return null;
        }
    }

    public async Task<Stream?> OpenReadAsync(string key, CancellationToken ct = default)
    {
        try
        {
            var response = await _s3.GetObjectAsync(_bucket, key, ct);
            return response.ResponseStream;
        }
        catch (AmazonS3Exception exception) when (IsNotFound(exception))
        {
            return null;
        }
    }
    public async Task<string?> GetTextAsync(string key, CancellationToken ct = default)
    {
        try
        {
            using var resp = await _s3.GetObjectAsync(_bucket, key, ct);
            using var reader = new StreamReader(resp.ResponseStream);
            return await reader.ReadToEndAsync(ct);
        }
        catch (AmazonS3Exception e) when (IsNotFound(e))
        {
            return null;
        }
    }

    public async Task<bool> TryPutTextAsync(string key, string text, CancellationToken ct = default)
    {
        await _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucket,
            Key = key,
            ContentBody = text,
            ContentType = "text/markdown; charset=utf-8"
        }, ct);
        return true;
    }

    public async Task<bool> TryPutStreamAsync(
        string key, Stream content, string? contentType, CancellationToken ct = default)
    {
        var request = new PutObjectRequest
        {
            BucketName = _bucket,
            Key = key,
            InputStream = content,
            AutoCloseStream = false
        };
        if (!string.IsNullOrWhiteSpace(contentType)) request.ContentType = contentType;
        await _s3.PutObjectAsync(request, ct);
        return true;
    }

    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        try
        {
            await _s3.DeleteObjectAsync(_bucket, key, ct);
        }
        catch (AmazonS3Exception e) when (IsNotFound(e))
        {
        }
    }

    private static bool IsNotFound(AmazonS3Exception exception) =>
        exception.StatusCode == System.Net.HttpStatusCode.NotFound || exception.ErrorCode == "NoSuchKey";
}
