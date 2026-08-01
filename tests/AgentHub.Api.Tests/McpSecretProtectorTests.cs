using AgentHub.Api.Library;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentHub.Api.Tests;

public class McpSecretProtectorTests
{
    private static IMcpSecretProtector CreateProtector()
    {
        var services = new ServiceCollection();
        services.AddDataProtection();
        var sp = services.BuildServiceProvider();
        return new McpSecretProtector(sp.GetRequiredService<IDataProtectionProvider>());
    }

    [Fact]
    public void Protect_Unprotect_RoundTripsPlaintext()
    {
        var protector = CreateProtector();
        const string plaintext = """{"token":"s3cr3t"}""";

        var protectedPayload = protector.Protect(plaintext);

        Assert.NotNull(protectedPayload);
        Assert.NotEqual(plaintext, protectedPayload);
        Assert.DoesNotContain("s3cr3t", protectedPayload);
        Assert.Equal(plaintext, protector.Unprotect(protectedPayload));
    }

    [Fact]
    public void Protect_NullOrEmpty_ReturnsNull()
    {
        var protector = CreateProtector();
        Assert.Null(protector.Protect(null));
        Assert.Null(protector.Protect(""));
        Assert.Null(protector.Unprotect(null));
        Assert.Null(protector.Unprotect(""));
    }
}
