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

    // #215: per-session limits. The defaults keep today's pod: limit 2 / 4Gi for every session.
    private static readonly ResourceBounds Cpu = new("cpu", "50m", "2", "8");
    private static readonly ResourceBounds Memory = new("memory", "256Mi", "4Gi", "16Gi");

    [Fact]
    public void ForCreate_WithoutLimit_KeepsTheInstanceMaximumAsRequestBound()
    {
        Assert.Null(SessionResources.ForCreate("2", null, Cpu));
        Assert.Null(SessionResources.ForCreate("1Gi", "", Memory));
        var error = Assert.Throws<ArgumentException>(() => SessionResources.ForCreate("3", null, Cpu));
        Assert.Contains("above the maximum of 2 on this instance", error.Message);
    }

    [Fact]
    public void ForCreate_WithLimit_RaisesTheRequestBoundUpToTheCeiling()
    {
        Assert.Equal("6", SessionResources.ForCreate("3", " 6 ", Cpu));
        Assert.Equal("12Gi", SessionResources.ForCreate("8Gi", "12Gi", Memory));
    }

    [Theory]
    [InlineData("500m", "9", "cpu limit '9' is above the ceiling of 8 on this instance")]
    [InlineData("500m", "10m", "cpu limit '10m' is below the minimum of 50m")]
    [InlineData("2", "1", "cpu request '2' is above this session's cpu limit of 1")]
    [InlineData("500m", "lots", "Invalid cpu limit quantity")]
    public void ForCreate_RejectsLimitsOutsideTheBoundsAndRequestsAboveTheLimit(string request, string limit, string expected)
    {
        var error = Assert.Throws<ArgumentException>(() => SessionResources.ForCreate(request, limit, Cpu));
        Assert.Contains(expected, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ForCreate_MemoryLimitInMilli_NamesTheUnitMistake()
    {
        var error = Assert.Throws<ArgumentException>(() => SessionResources.ForCreate("1Gi", "8m", Memory));
        Assert.Contains("The suffix m means milli", error.Message);
    }

    [Fact]
    public void ForUpdate_LoweringTheLimitBelowTheStoredRequest_IsRefused()
    {
        var error = Assert.Throws<ArgumentException>(() => SessionResources.ForUpdate("2Gi", null, null, "1Gi", Memory));
        Assert.Contains("below this session's memory request of 2Gi", error.Message);
        Assert.Equal(("1Gi", "1Gi"), SessionResources.ForUpdate("2Gi", null, "1Gi", "1Gi", Memory));
    }

    [Fact]
    public void ForUpdate_UnchangedValues_AreNotRechecked()
    {
        // A legacy row (#162) and a stored limit above a since-lowered ceiling both stay editable.
        Assert.Equal(("10m", "20Gi"), SessionResources.ForUpdate("10m", "20Gi", "10m", "20Gi", Memory));
        Assert.Equal(("1Gi", null), SessionResources.ForUpdate("1Gi", null, null, "", Memory));
    }

    [Fact]
    public void ForUpdate_EmptyLimit_ResetsToTheDefaultAndChecksTheRequestAgainstIt()
    {
        Assert.Equal(("1", null), SessionResources.ForUpdate("1", "6", null, "", Cpu));
        var error = Assert.Throws<ArgumentException>(() => SessionResources.ForUpdate("4", "6", null, "", Cpu));
        Assert.Contains("cpu limit 2 is below this session's cpu request of 4", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("500m", null, "500m", "2")]
    [InlineData("3", "6", "3", "6")]
    [InlineData("6", "12", "6", "8")]   // ceiling lowered after the session was stored
    [InlineData("4", "1", "1", "1")]    // request follows a clamped limit down
    [InlineData("1m", "10m", "50m", "50m")]
    public void EffectivePair_ClampsStoredValuesIntoTheBounds(string request, string? limit, string wantRequest, string wantLimit) =>
        Assert.Equal((wantRequest, wantLimit), SessionResources.EffectivePair(request, limit, Cpu));

    [Fact]
    public void Bounds_EmptyCeilingIsTheMaximum_AndALowerCeilingWins()
    {
        var o = new AgentHubOptions();
        Assert.Equal(new ResourceBounds("cpu", "50m", "2", "2"), ResourceBounds.Cpu(o));
        Assert.Equal(new ResourceBounds("memory", "256Mi", "4Gi", "4Gi"), ResourceBounds.Memory(o));
        o.MemoryLimitCeiling = "2Gi";
        Assert.Equal(new ResourceBounds("memory", "256Mi", "2Gi", "2Gi"), ResourceBounds.Memory(o));
    }
}
