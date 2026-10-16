using AgentHub.Api.Services;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>
/// The version and repository link that <c>GET /api/config</c> hands the UI. The endpoint is a
/// minimal-API lambda in Program.cs that reads <see cref="AppVersion.Current"/> verbatim, so the
/// resolution rules are what needs proving here.
/// </summary>
public class AppVersionTests
{
    [Fact]
    public void Current_IsNeverEmpty_SoTheFooterAlwaysHasALabel()
    {
        Assert.False(string.IsNullOrWhiteSpace(AppVersion.Current));
    }

    [Fact]
    public void RepoUrl_PointsAtTheProjectRepository()
    {
        Assert.Equal("https://github.com/open-agenthub/open-agenthub", AppVersion.RepoUrl);
    }

    [Theory]
    [InlineData("0.12.0", "0.12.0")]
    [InlineData("  0.12.0 ", "0.12.0")]
    [InlineData("a1b2c3d", "a1b2c3d")]
    public void Resolve_UsesTheBakedInformationalVersion(string baked, string expected)
    {
        Assert.Equal(expected, AppVersion.Resolve(null, baked));
    }

    [Theory]
    [InlineData("0.12.0+9f8e7d6")]
    [InlineData("0.12.0+9f8e7d6.dirty")]
    public void Resolve_StripsTheCommitSuffixSourceLinkAppends(string baked)
    {
        Assert.Equal("0.12.0", AppVersion.Resolve(null, baked));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("+abc")]
    public void Resolve_FallsBackToDev_WhenNothingWasBakedIn(string? baked)
    {
        Assert.Equal(AppVersion.Dev, AppVersion.Resolve(null, baked));
    }

    [Fact]
    public void Resolve_PrefersTheEnvironmentOverride_SoACustomBuildCanBeLabelled()
    {
        Assert.Equal("0.12.0-custom", AppVersion.Resolve("0.12.0-custom", "0.12.0"));
        Assert.Equal("0.12.0-custom", AppVersion.Resolve(" 0.12.0-custom ", null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Resolve_IgnoresABlankOverride_InsteadOfHidingTheBakedVersion(string overrideValue)
    {
        Assert.Equal("0.12.0", AppVersion.Resolve(overrideValue, "0.12.0"));
    }

    [Fact]
    public void OverrideVariable_IsTheDocumentedName()
    {
        Assert.Equal("AGENTHUB_VERSION", AppVersion.OverrideVariable);
    }
}
