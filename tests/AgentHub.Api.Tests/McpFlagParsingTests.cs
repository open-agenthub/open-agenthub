using System.Reflection;
using AgentHub.Api.Mcp;
using Xunit;

namespace AgentHub.Api.Tests;

public class McpFlagParsingTests
{
    // Boolean tool parameters are declared as strings because an MCP client caches the tool
    // schema at connect time: a parameter added later arrives as text from every client that is
    // already connected. Declared as bool, the whole call failed with a JSON conversion error
    // until the client reconnected.
    private static bool? Parse(string? value) =>
        (bool?)typeof(AgentHubMcpTools)
            .GetMethod("ParseFlag", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [value]);

    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("  true  ", true)]
    [InlineData("yes", true)]
    [InlineData("1", true)]
    [InlineData("on", true)]
    [InlineData("false", false)]
    [InlineData("FALSE", false)]
    [InlineData("no", false)]
    [InlineData("0", false)]
    [InlineData("off", false)]
    public void Recognises_the_usual_spellings(string value, bool expected)
        => Assert.Equal(expected, Parse(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    // Anything unrecognised means "not specified" rather than false, so a typo cannot silently
    // turn an unattended session's auto-approve off and leave it stalling on a prompt.
    [InlineData("maybe")]
    public void Anything_else_means_not_specified(string? value)
        => Assert.Null(Parse(value));
}
