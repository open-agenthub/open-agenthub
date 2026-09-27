using AgentHub.Api.Storage;
using Xunit;

namespace AgentHub.Api.Tests;

public class S3PresignSchemeTests
{
    // The AWS SDK decides a presigned url's scheme from AmazonS3Config.UseHttp, not from
    // ServiceURL, and it defaults to https. An http endpoint signed as https hands the session
    // pod a url whose TLS handshake fails against a plain-HTTP port — the pod then starts
    // without its history and never writes any back, with storage looking simply empty.
    [Theory]
    [InlineData("http://garage.garage.svc.cluster.local:3900", true)]
    [InlineData("http://minio.minio.svc.cluster.local:9000", true)]
    [InlineData("HTTP://Garage.Local:3900", true)]
    [InlineData("  http://garage.local:3900  ", true)]
    [InlineData("https://s3.example.com", false)]
    [InlineData("https://garage.example.com:3900", false)]
    // An empty ServiceUrl means AWS's own endpoints, which are https.
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Presigned_urls_use_http_only_for_a_plain_http_endpoint(string? serviceUrl, bool expected)
        => Assert.Equal(expected, S3ArtifactStore.IsPlainHttp(serviceUrl));
}
