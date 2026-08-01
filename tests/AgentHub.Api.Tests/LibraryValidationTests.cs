using AgentHub.Api.Library;
using Xunit;

namespace AgentHub.Api.Tests;

public class LibraryValidationTests
{
    [Fact]
    public void ValidateMcpServerName_rejects_empty()
    {
        Assert.Throws<ArgumentException>(() => LibraryValidation.ValidateMcpServerName(""));
    }

    [Fact]
    public void ValidateRawConfig_requires_object()
    {
        Assert.Throws<ArgumentException>(() =>
            LibraryValidation.ValidateMcpServerConfig("{", kind: "raw"));
    }

    [Fact]
    public void ValidateApiConfig_requires_specUrl()
    {
        Assert.Throws<ArgumentException>(() =>
            LibraryValidation.ValidateMcpServerConfig(
                """{"specType":"openapi"}""", kind: "api"));
    }
}
