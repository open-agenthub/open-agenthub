using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentHub.Api.Licensing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentHub.Api.Tests;

public class EnterpriseLicenseTests
{
    private static EnterpriseLicense Build(params (string, string?)[] settings)
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Item1, s.Item2)))
            .Build();
        return new EnterpriseLicense(cfg, NullLogger<EnterpriseLicense>.Instance);
    }

    // Signs a token exactly like the license service (ES256 over header.payload, raw r||s).
    private static (string token, string publicKeyPem) IssueToken(int seats, DateTime validUntil, string plan = "subscription")
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var header = B64(JsonSerializer.SerializeToUtf8Bytes(new { alg = "ES256", typ = "LICENSE" }));
        var payload = B64(JsonSerializer.SerializeToUtf8Bytes(new
        {
            org = "Acme", email = "a@b.c", seats, plan,
            validUntil = validUntil.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")
        }));
        var sig = ecdsa.SignData(Encoding.ASCII.GetBytes($"{header}.{payload}"),
            HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return ($"{header}.{payload}.{B64(sig)}", ecdsa.ExportSubjectPublicKeyInfoPem());
    }

    [Fact]
    public void NoToken_IsInvalid()
    {
        var lic = Build();
        Assert.False(lic.Enabled);
        Assert.Contains("No license", lic.Status.Reason);
    }

    [Fact]
    public void ValidToken_IsAccepted()
    {
        var (token, pub) = IssueToken(seats: 25, validUntil: DateTime.UtcNow.AddDays(30));
        var lic = Build(("License:Token", token), ("License:PublicKey", pub));
        Assert.True(lic.Enabled);
        Assert.Equal(25, lic.Status.Seats);
        Assert.Equal("subscription", lic.Status.Plan);
    }

    [Fact]
    public void ExpiredToken_IsInvalid()
    {
        var (token, pub) = IssueToken(seats: 5, validUntil: DateTime.UtcNow.AddDays(-1));
        var lic = Build(("License:Token", token), ("License:PublicKey", pub));
        Assert.False(lic.Enabled);
        Assert.Contains("expired", lic.Status.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WrongKey_IsRejected()
    {
        var (token, _) = IssueToken(seats: 5, validUntil: DateTime.UtcNow.AddDays(30));
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var lic = Build(("License:Token", token), ("License:PublicKey", other.ExportSubjectPublicKeyInfoPem()));
        Assert.False(lic.Enabled);
    }
}
