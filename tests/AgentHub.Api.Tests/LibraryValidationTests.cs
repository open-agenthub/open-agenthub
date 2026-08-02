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
    public void ValidateRawConfig_requires_object()
    {
        Assert.Throws<ArgumentException>(() =>
            LibraryValidation.ValidateMcpServerConfig("{", kind: "raw"));
        Assert.Throws<ArgumentException>(() =>
            LibraryValidation.ValidateMcpServerConfig("{\"mcpServers\":{\"a\":{}}}", kind: "raw"));
        Assert.Equal("{\"type\":\"http\"}",
            LibraryValidation.ValidateMcpServerConfig("{\"type\":\"http\"}", kind: "raw"));
    }

    [Fact]
    public void ValidateApiConfig_requires_specUrl()
    {
        Assert.Throws<ArgumentException>(() =>
            LibraryValidation.ValidateMcpServerConfig(
                """{"specType":"openapi"}""", kind: "api"));
    }

    [Fact]
    public void ValidateApiConfig_rejects_localhost_specUrl()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            LibraryValidation.ValidateMcpServerConfig(
                """{"specType":"openapi","specUrl":"http://localhost/openapi.json"}""",
                kind: "api"));
        Assert.Contains("blocked", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SkillContent_Limits()
    {
        Assert.Throws<ArgumentException>(() => LibraryValidation.ValidateSkillContent("  "));
        Assert.Throws<ArgumentException>(() =>
            LibraryValidation.ValidateSkillContent(new string('x', LibraryValidation.MaxSkillContentChars + 1)));
        Assert.Equal("# hello", LibraryValidation.ValidateSkillContent("# hello"));
    }

    [Fact]
    public void SkillFilePaths_MustBeRelativeShallowAndSafe()
    {
        Assert.Equal("scripts/check.sh", LibraryValidation.ValidateSkillFilePath("scripts/check.sh"));
        Assert.Equal("a/b/c.txt", LibraryValidation.ValidateSkillFilePath("a\\b\\c.txt"));

        foreach (var bad in new[]
                 {
                     "", "../escape.sh", "a/../b", "/abs.sh", ".hidden", "dir/.env",
                     "a/b/c/d.txt", "space in name.sh", "SKILL.md", "skill.MD"
                 })
        {
            Assert.Throws<ArgumentException>(() => LibraryValidation.ValidateSkillFilePath(bad));
        }
    }

    [Fact]
    public void SkillFiles_EnforceCountDuplicateAndSizeLimits()
    {
        Assert.Null(LibraryValidation.ValidateSkillFiles(null));
        Assert.Empty(LibraryValidation.ValidateSkillFiles([])!);

        Assert.Throws<ArgumentException>(() => LibraryValidation.ValidateSkillFiles(
            [new SkillFile("a.sh", "x"), new SkillFile("a.sh", "y")]));
        Assert.Throws<ArgumentException>(() => LibraryValidation.ValidateSkillFiles(
            [new SkillFile("bin.dat", "has\0null")]));
        Assert.Throws<ArgumentException>(() => LibraryValidation.ValidateSkillFiles(
            Enumerable.Range(0, LibraryValidation.MaxSkillFiles + 1)
                .Select(i => new SkillFile($"f{i}.txt", "x")).ToList()));
    }
}
