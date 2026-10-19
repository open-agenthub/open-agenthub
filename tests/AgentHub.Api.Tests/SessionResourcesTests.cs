using AgentHub.Api.Services;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>Session CPU/memory bounds (#162, docs/session-resources.md).</summary>
public class SessionResourcesTests
{
    [Theory]
    [InlineData("50m", "cpu")]
    [InlineData("500m", "cpu")]
    [InlineData("2", "cpu")]
    [InlineData("256Mi", "memory")]
    [InlineData("1Gi", "memory")]
    [InlineData("4Gi", "memory")]
    public void Validate_AcceptsValuesWithinTheBounds(string value, string what)
    {
        var (min, max) = what == "cpu" ? ("50m", "2") : ("256Mi", "4Gi");
        SessionResources.Validate(value, what, min, max);
    }

    [Fact]
    public void Validate_MemoryInMilli_NamesTheUnitMistake()
    {
        var error = Assert.Throws<ArgumentException>(() => SessionResources.Validate("10m", "memory", "256Mi", "4Gi"));
        Assert.Contains("below the minimum of 256Mi", error.Message);
        Assert.Contains("The suffix m means milli", error.Message);
    }

    [Theory]
    [InlineData("1Mi", "memory", "below the minimum of 256Mi")]
    [InlineData("8Gi", "memory", "above the maximum of 4Gi")]
    [InlineData("1m", "cpu", "below the minimum of 50m")]
    [InlineData("8", "cpu", "above the maximum of 2")]
    [InlineData("lots", "memory", "Invalid memory quantity")]
    [InlineData("", "cpu", "Invalid cpu quantity")]
    public void Validate_RejectsWithAClearMessage(string value, string what, string expected)
    {
        var (min, max) = what == "cpu" ? ("50m", "2") : ("256Mi", "4Gi");
        var error = Assert.Throws<ArgumentException>(() => SessionResources.Validate(value, what, min, max));
        Assert.Contains(expected, error.Message);
        // Only memory written in milli gets the unit hint.
        Assert.Equal(value == "10m", error.Message.Contains("milli"));
    }

    [Theory]
    [InlineData("10m", "256Mi")]
    [InlineData("1Mi", "256Mi")]
    [InlineData("8Gi", "4Gi")]
    [InlineData("garbage", "256Mi")]
    [InlineData(null, "256Mi")]
    [InlineData(" 512Mi ", "512Mi")]
    public void Effective_ClampsStoredValuesIntoTheBounds(string? stored, string expected) =>
        Assert.Equal(expected, SessionResources.Effective(stored, "256Mi", "4Gi"));
}
