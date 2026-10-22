using AgentHub.Api.Models;
using AgentHub.Api.Services;
using Xunit;

namespace AgentHub.Api.Tests;

public sealed class SessionScheduleTests
{
    [Theory]
    [InlineData("")]
    [InlineData("60 * * * *")]
    [InlineData("* * * *")]
    [InlineData("0 0 0 * * *")]
    [InlineData("TZ=Etc/UTC 0 0 * * *")]
    public void CreateAndUpdateRejectInvalidSchedules(string expression)
        => Assert.Throws<ArgumentException>(() => SessionSchedule.Normalize(SessionMode.Scheduled, expression));

    [Fact]
    public void ScheduleRequiresScheduledMode()
    {
        Assert.Throws<ArgumentException>(() => SessionSchedule.Normalize(SessionMode.Interactive, "0 6 * * *"));
        Assert.Throws<ArgumentException>(() => SessionSchedule.Normalize(SessionMode.Scheduled, null));
        Assert.Null(SessionSchedule.Normalize(SessionMode.Interactive, null));
    }

    [Theory]
    [InlineData("0 6 * * 1-5", "2026-10-23T06:00:00Z")]
    [InlineData("@daily", "2026-10-23T00:00:00Z")]
    [InlineData("0 6 1 * 1", "2026-10-26T06:00:00Z")]
    [InlineData("0 6 */2 * 1", "2026-10-23T06:00:00Z")]
    [InlineData("0 6 ? * MON", "2026-10-26T06:00:00Z")]
    public void NextOccurrenceFollowsUtcAndControllerDaySemantics(string expression, string expected)
        => Assert.Equal(DateTimeOffset.Parse(expected), SessionSchedule.NextRun(expression, DateTimeOffset.Parse("2026-10-22T10:00:00Z")));

    [Fact]
    public void InvalidLegacyScheduleDoesNotBreakListing()
        => Assert.Null(SessionSchedule.NextRun("invalid", DateTimeOffset.UtcNow));

    [Fact]
    public void RemoteUpdateForwardsSchedule()
        => Assert.Equal("0 9 * * *", new RemoteUpdateSessionRequest { Schedule = "0 9 * * *" }.ToUpdate().Schedule);
}
