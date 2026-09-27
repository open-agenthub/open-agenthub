using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Npgsql;

namespace AgentHub.Api.Mcp;

/// <summary>
/// The certificates the OAuth server signs and encrypts its tokens with, persisted in Postgres.
///
/// OpenIddict's AddDevelopment*Certificate helpers cannot be used here: they write the generated
/// certificate into the user's X509 store, which is a path on disk, and the backend container
/// runs with a read-only root filesystem — the write throws on every request that touches the
/// OAuth endpoints. Keeping the certificates in the database also means every replica signs with
/// the same key, so tokens issued by one are accepted by the others, and they survive a restart
/// instead of silently invalidating every client's session.
/// </summary>
public sealed class McpSigningKeyStore
{
    private const string SigningId = "signing";
    private const string EncryptionId = "encryption";

    private readonly NpgsqlDataSource _db;

    public McpSigningKeyStore(IConfiguration cfg)
    {
        var cs = cfg.GetConnectionString("Postgres")
                 ?? throw new InvalidOperationException("ConnectionStrings:Postgres is missing.");
        _db = NpgsqlDataSource.Create(cs);
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        const string ddl = """
            CREATE TABLE IF NOT EXISTS mcp_signing_keys (
                id         TEXT PRIMARY KEY,
                pkcs12     BYTEA NOT NULL,
                created_at TIMESTAMPTZ NOT NULL DEFAULT now()
            );
            """;
        await using var cmd = _db.CreateCommand(ddl);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public Task<X509Certificate2> GetSigningCertificateAsync(CancellationToken ct = default)
        => GetOrCreateAsync(SigningId, X509KeyUsageFlags.DigitalSignature, "AgentHub MCP Signing", ct);

    public Task<X509Certificate2> GetEncryptionCertificateAsync(CancellationToken ct = default)
        => GetOrCreateAsync(EncryptionId, X509KeyUsageFlags.KeyEncipherment, "AgentHub MCP Encryption", ct);

    private async Task<X509Certificate2> GetOrCreateAsync(
        string id, X509KeyUsageFlags usage, string subject, CancellationToken ct)
    {
        if (await LoadAsync(id, ct) is { } existing) return existing;

        var pkcs12 = Create(usage, subject);
        // Two replicas starting together would both generate one; the first insert wins and the
        // loser re-reads it, so they cannot end up signing with different keys.
        await using var insert = _db.CreateCommand(
            "INSERT INTO mcp_signing_keys (id, pkcs12) VALUES (@id, @pkcs12) ON CONFLICT (id) DO NOTHING;");
        insert.Parameters.AddWithValue("id", id);
        insert.Parameters.AddWithValue("pkcs12", pkcs12);
        await insert.ExecuteNonQueryAsync(ct);

        return await LoadAsync(id, ct)
               ?? throw new InvalidOperationException($"The MCP {id} certificate could not be stored.");
    }

    private async Task<X509Certificate2?> LoadAsync(string id, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand("SELECT pkcs12 FROM mcp_signing_keys WHERE id=@id;");
        cmd.Parameters.AddWithValue("id", id);
        if (await cmd.ExecuteScalarAsync(ct) is not byte[] pkcs12) return null;
        // EphemeralKeySet keeps the private key in memory. Without it the loader spills the key
        // to disk, which is the very failure this store exists to avoid.
        return X509CertificateLoader.LoadPkcs12(pkcs12, null, X509KeyStorageFlags.EphemeralKeySet);
    }

    private static byte[] Create(X509KeyUsageFlags usage, string subject)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            new X500DistinguishedName($"CN={subject}"), rsa,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(usage, critical: true));

        // Backdated so a small clock skew between replicas cannot make a fresh certificate look
        // not-yet-valid. Long lived because rotating it invalidates every issued token.
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddYears(5));
        return certificate.Export(X509ContentType.Pkcs12);
    }
}
