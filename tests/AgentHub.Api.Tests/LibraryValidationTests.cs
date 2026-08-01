using AgentHub.Api.Library;
using Xunit;

namespace AgentHub.Api.Tests;

public class LibraryValidationTests
{
    [Theory]
    [InlineData("docs")]
    [InlineData("my-server.v2")]
    public void McpServerName_Valid(string name) =>
        Assert.Equal(name, LibraryValidation.ValidateMcpServerName($" {name} "));

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("mcp__docs")]
    [InlineData("a__b")]
    public void McpServerName_Invalid(string name) =>
        Assert.Throws<ArgumentException>(() => LibraryValidation.ValidateMcpServerName(name));

    [Theory]
    [InlineData("code-review")]
    [InlineData("a")]
    [InlineData("skill2")]
    public void SkillName_Valid(string name) =>
        Assert.Equal(name, LibraryValidation.ValidateSkillName(name));

    [Theory]
    [InlineData("")]
    [InlineData("Upper")]
    [InlineData("-leading")]
    [InlineData("trailing-")]
    [InlineData("has space")]
    [InlineData("dots.not.allowed")]
    public void SkillName_Invalid(string name) =>
        Assert.Throws<ArgumentException>(() => LibraryValidation.ValidateSkillName(name));

    [Fact]
    public void McpServerConfig_MustBeSingleServerObject()
    {
        Assert.Throws<ArgumentException>(() => LibraryValidation.ValidateMcpServerConfig(""));
        Assert.Throws<ArgumentException>(() => LibraryValidation.ValidateMcpServerConfig("not json"));
        Assert.Throws<ArgumentException>(() => LibraryValidation.ValidateMcpServerConfig("[1]"));
        // A full .mcp.json is rejected so users don't nest configs by accident.
        Assert.Throws<ArgumentException>(() =>
            LibraryValidation.ValidateMcpServerConfig("{\"mcpServers\":{\"a\":{}}}"));
        Assert.Equal("{\"type\":\"http\"}", LibraryValidation.ValidateMcpServerConfig("{\"type\":\"http\"}"));
    }

    [Fact]
    public void SkillContent_Limits()
    {
        Assert.Throws<ArgumentException>(() => LibraryValidation.ValidateSkillContent("  "));
        Assert.Throws<ArgumentException>(() =>
            LibraryValidation.ValidateSkillContent(new string('x', LibraryValidation.MaxSkillContentChars + 1)));
        Assert.Equal("# hello", LibraryValidation.ValidateSkillContent("# hello"));
    }
}
