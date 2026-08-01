using AgentHub.Api.Library;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentHub.Api.Tests;

public class McpGatewayTokenServiceTests
{
    private static IMcpGatewayTokenService Create()
    {
        var services = new ServiceCollection();
        services.AddDataProtection();
        var sp = services.BuildServiceProvider();
        return new McpGatewayTokenService(sp.GetRequiredService<IDataProtectionProvider>());
    }

    [Fact]
    public void Issue_Validate_RoundTripsClaims()
    {
        var svc = Create();
        var token = svc.Issue("sess-1", "mcp-42", "alice");

        Assert.True(svc.TryValidate(token, "mcp-42", out var claims));
        Assert.Equal("sess-1", claims.SessionId);
        Assert.Equal("mcp-42", claims.McpServerId);
        Assert.Equal("alice", claims.Owner);
        Assert.True(claims.ExpiresAt > DateTimeOffset.UtcNow);
    }

    [Fact]
    public void Validate_RejectsWrongMcpServerId()
    {
        var svc = Create();
        var token = svc.Issue("sess-1", "mcp-42", "alice");

        Assert.False(svc.TryValidate(token, "other-id", out _));
    }

    [Fact]
    public void Validate_RejectsGarbage()
    {
        var svc = Create();
        Assert.False(svc.TryValidate("", "mcp-42", out _));
        Assert.False(svc.TryValidate("not-a-token", "mcp-42", out _));
    }
}
