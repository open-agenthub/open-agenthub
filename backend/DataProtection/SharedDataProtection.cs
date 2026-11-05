using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;

namespace AgentHub.Api.DataProtection;

public static class SharedDataProtection
{
    public const string ApplicationName = "open-agenthub";

    public static void AddSharedDataProtection(this IServiceCollection services, IConfiguration configuration)
    {
        var certificate = LoadCertificate(configuration.GetSection("DataProtection"));
        var certificates = new[] { certificate }.Concat(configuration
            .GetSection("DataProtection:PreviousCertificates").GetChildren().Select(LoadCertificate)).ToArray();
        services.AddSingleton<PostgresKeyRepository>();
        services.AddDataProtection().SetApplicationName(ApplicationName)
            .ProtectKeysWithCertificate(certificate).UnprotectKeysWithAnyCertificate(certificates);
        services.AddOptions<KeyManagementOptions>().Configure<PostgresKeyRepository>(
            (options, repository) => options.XmlRepository = repository);
    }

    private static X509Certificate2 LoadCertificate(IConfiguration section)
    {
        if (string.IsNullOrWhiteSpace(section["CertificatePem"]) || string.IsNullOrWhiteSpace(section["PrivateKeyPem"]))
            throw new InvalidOperationException(
                "DataProtection:CertificatePem and DataProtection:PrivateKeyPem are required. " +
                "Configure the same key-encryption certificate on every backend replica; retain it with database backups.");
        try
        {
            var certificate = X509Certificate2.CreateFromPem(section["CertificatePem"]!, section["PrivateKeyPem"]!);
            using var rsa = certificate.GetRSAPrivateKey();
            if (rsa is null || rsa.KeySize < 2048) throw new CryptographicException();
            return certificate;
        }
        catch (CryptographicException)
        {
            // Never put certificate input or private-key parser details into a startup error.
            throw new InvalidOperationException("DataProtection requires a matching PEM certificate and RSA private key of at least 2048 bits.");
        }
    }
}
