using AgentHub.Api.Permissions;
using Xunit;

namespace AgentHub.Api.Tests;

public class PermissionRequestDescriptorTests
{
    [Theory]
    [InlineData("Bash", "Bash command")]
    [InlineData("mcp__docs__search", "MCP tool request")]
    [InlineData("apply_patch", "File change request")]
    [InlineData("Edit", "File change request")]
    [InlineData("Write", "File change request")]
    [InlineData("Read", "Tool request")]
    [InlineData(null, "Tool request")]
    public void ForTool_ReturnsOnlyFixedNonSensitiveCategories(string? tool, string expected)
    {
        var descriptor = PermissionRequestDescriptor.ForTool(tool);

        Assert.Equal(expected, descriptor);
        Assert.True(descriptor.Length <= 32);
    }

    [Fact]
    public void ForTool_DoesNotEchoUntrustedToolText()
    {
        const string secret = "sensitive-tool-fixture";

        Assert.DoesNotContain(secret, PermissionRequestDescriptor.ForTool(secret), StringComparison.Ordinal);
    }
}
