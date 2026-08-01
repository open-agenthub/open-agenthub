using Amazon.S3;
using Amazon.S3.Model;
using AgentHub.Api.Models;

namespace AgentHub.Api.Storage;

/// <summary>
/// Storage in S3 (or MinIO). The agent pod never receives S3 credentials,
/// only time-limited presigned URLs.
/// Key layout: sessions/{owner}/{sessionId}/{state.tgz|scrollback.log|artifacts/...}
/// </summary>
public interface IArtifactStore
{
    string PresignPut(string key, TimeSpan ttl);
    string PresignGet(string key, TimeSpan ttl);
    Task<string?> GetTextAsync(string key, CancellationToken ct = default);
    /// <summary>Writes text content. Returns false when no object storage is configured
    /// (NullArtifactStore) so callers can fall back to database storage.</summary>
    Task<bool> TryPutTextAsync(string key, string text, CancellationToken ct = default);
    /// <summary>Best-effort delete; missing objects are not an error.</summary>
    Task DeleteAsync(string key, CancellationToken ct = default);

    static string StateKey(string owner, string id) => StateKey(owner, id, AgentKind.Claude);
    static string StateKey(string owner, string id, AgentKind agent) =>
        $"sessions/{owner}/{id}/{agent switch
        {
            AgentKind.Claude => "claude-state.tgz",
            AgentKind.Codex => "codex-state.tgz",
            AgentKind.Cursor => "cursor-state.tgz",
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
}

/// <summary>
/// Fallback when S3 is not configured: no persistence of state/scrollback/artifacts.
/// Sessions run normally, but resume and the transcript view are disabled.
/// </summary>
public sealed class NullArtifactStore : IArtifactStore
{
    public string PresignPut(string key, TimeSpan ttl) => "";
    public string PresignGet(string key, TimeSpan ttl) => "";
    public Task<string?> GetTextAsync(string key, CancellationToken ct = default) => Task.FromResult<string?>(null);
    public Task<bool> TryPutTextAsync(string key, string text, CancellationToken ct = default) => Task.FromResult(false);
    public Task DeleteAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
}

public sealed class S3ArtifactStore : IArtifactStore
{
    private readonly IAmazonS3 _s3;
    private readonly string _bucket;

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
        _s3.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = _bucket,
            Key = key,
            Verb = verb,
            Expires = DateTime.UtcNow.Add(ttl)
        });

    public async Task<string?> GetTextAsync(string key, CancellationToken ct = default)
    {
        try
        {
            using var resp = await _s3.GetObjectAsync(_bucket, key, ct);
            using var reader = new StreamReader(resp.ResponseStream);
            return await reader.ReadToEndAsync(ct);
        }
        catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
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

    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        try
        {
            await _s3.DeleteObjectAsync(_bucket, key, ct);
        }
        catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
        }
    }
}
