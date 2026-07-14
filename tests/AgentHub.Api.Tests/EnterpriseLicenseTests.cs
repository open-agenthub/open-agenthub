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
    // In-memory license store so the token verification can be tested without Postgres.
    private sealed class FakeStore(string? token) : ILicenseStore
    {
        public string? Token = token;
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetTokenAsync(CancellationToken ct = default) => Task.FromResult(Token);
        public Task SetTokenAsync(string? t, CancellationToken ct = default) { Token = t; return Task.CompletedTask; }
    }

    // Builds a license backed by the given token (in the store) and public key (in config), reloaded.
    private static EnterpriseLicense Build(string? token = null, string? publicKey = null)
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new[] { new KeyValuePair<string, string?>("License:PublicKey", publicKey) })
            .Build();
        var lic = new EnterpriseLicense(cfg, new FakeStore(token), NullLogger<EnterpriseLicense>.Instance);
        lic.ReloadAsync().GetAwaiter().GetResult();
        return lic;
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
        Assert.False(lic.Status.Present);
        Assert.Contains("No license", lic.Status.Reason);
    }

    [Fact]
    public void ValidToken_IsAccepted()
    {
        var (token, pub) = IssueToken(seats: 25, validUntil: DateTime.UtcNow.AddDays(30));
        var lic = Build(token, pub);
        Assert.True(lic.Enabled);
        Assert.True(lic.Status.Present);
        Assert.Equal(25, lic.Status.Seats);
        Assert.Equal("subscription", lic.Status.Plan);
    }

    [Fact]
    public void ExpiredToken_IsInvalid()
    {
        var (token, pub) = IssueToken(seats: 5, validUntil: DateTime.UtcNow.AddDays(-1));
        var lic = Build(token, pub);
        Assert.False(lic.Enabled);
        Assert.Contains("expired", lic.Status.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WrongKey_IsRejected()
    {
        var (token, _) = IssueToken(seats: 5, validUntil: DateTime.UtcNow.AddDays(30));
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var lic = Build(token, other.ExportSubjectPublicKeyInfoPem());
        Assert.False(lic.Enabled);
        Assert.True(lic.Status.Present);   // a token is stored, it just doesn't verify
    }

    [Fact]
    public async Task Reload_PicksUpNewlyActivatedToken()
    {
        var (token, pub) = IssueToken(seats: 10, validUntil: DateTime.UtcNow.AddDays(30));
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new[] { new KeyValuePair<string, string?>("License:PublicKey", pub) })
            .Build();
        var store = new FakeStore(null);
        var lic = new EnterpriseLicense(cfg, store, NullLogger<EnterpriseLicense>.Instance);
        await lic.ReloadAsync();
        Assert.False(lic.Enabled);

        // Simulate admin activation, then reload.
        await store.SetTokenAsync(token);
        await lic.ReloadAsync();
        Assert.True(lic.Enabled);
        Assert.Equal(10, lic.Status.Seats);
    }
}
