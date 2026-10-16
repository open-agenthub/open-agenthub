using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>
/// The pure rules behind a session that deletes itself (docs/session-expiry.md): the duration
/// grammar the MCP tools accept, the limits, the scheduled-session basis rule, the partial update
/// semantics and the deadline arithmetic the sweeper and the UI both rely on.
/// </summary>
public class SessionExpiryTests
{
    [Theory]
    [InlineData("90m", 5400)]
    [InlineData("12h", 43200)]
    [InlineData("3d", 259200)]
    [InlineData("300s", 300)]
    [InlineData(" 2H ", 7200)]
    [InlineData("365d", 31536000)]
    public void ParseDuration_ReadsNumberAndUnit(string text, int expected)
        => Assert.Equal(expected, SessionExpiry.ParseDuration(text));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("12")]        // unit mandatory: a bare number could be minutes or seconds
    [InlineData("1.5h")]
    [InlineData("2 days")]
    [InlineData("1w")]
    [InlineData("4m")]        // under the five-minute floor
    [InlineData("366d")]      // over the one-year ceiling
    [InlineData("9999999999d")]
    public void ParseDuration_RejectsWhatItCannotReadOrAllow(string? text)
        => Assert.Throws<ArgumentException>(() => SessionExpiry.ParseDuration(text));

    [Theory]
    [InlineData(299)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(31536001)]
    public void ValidateSeconds_RejectsOutsideTheRange(int seconds)
        => Assert.Throws<ArgumentException>(() => SessionExpiry.ValidateSeconds(seconds));

    [Fact]
    public void NormalizeFrom_DefaultsToLastActivity_AndIsCaseInsensitive()
    {
        Assert.Equal("lastActivity", SessionExpiry.NormalizeFrom(null, SessionMode.Interactive));
        Assert.Equal("lastActivity", SessionExpiry.NormalizeFrom(" LASTACTIVITY ", SessionMode.Autonomous));
        Assert.Equal("start", SessionExpiry.NormalizeFrom("Start", SessionMode.Interactive));
        Assert.Throws<ArgumentException>(() => SessionExpiry.NormalizeFrom("creation", SessionMode.Interactive));
    }

    [Fact]
    public void NormalizeFrom_OnAScheduledSession_DefaultsToStartAndRefusesLastActivity()
    {
        // Nobody attaches to a CronJob, so "last activity" would be its creation and the session
        // would vanish on the first idle interval. The default is corrected; an explicit ask is
        // refused so an API caller learns the rule.
        Assert.Equal("start", SessionExpiry.NormalizeFrom(null, SessionMode.Scheduled));
        Assert.Equal("start", SessionExpiry.NormalizeFrom("start", SessionMode.Scheduled));
        Assert.Throws<ArgumentException>(() => SessionExpiry.NormalizeFrom("lastActivity", SessionMode.Scheduled));
    }

    [Fact]
    public void ForCreate_OffMeansNoBasisEither()
    {
        Assert.Equal((null, null), SessionExpiry.ForCreate(null, "start", SessionMode.Interactive));
        Assert.Equal((null, null), SessionExpiry.ForCreate(0, "start", SessionMode.Interactive));
        Assert.Equal((3600, "lastActivity"), SessionExpiry.ForCreate(3600, null, SessionMode.Interactive));
        Assert.Throws<ArgumentException>(() => SessionExpiry.ForCreate(60, null, SessionMode.Interactive));
    }

    [Fact]
    public void ForUpdate_NullLeavesAlone_ZeroSwitchesOff_BasisAloneKeepsTheDeadline()
    {
        var record = Record(SessionMode.Interactive, 7200, "lastActivity");

        Assert.Equal((7200, "lastActivity"), SessionExpiry.ForUpdate(record, null, null));
        Assert.Equal((null, null), SessionExpiry.ForUpdate(record, 0, "start"));
        Assert.Equal((7200, "start"), SessionExpiry.ForUpdate(record, null, "start"));
        Assert.Equal((600, "lastActivity"), SessionExpiry.ForUpdate(record, 600, null));
        Assert.Throws<ArgumentException>(() => SessionExpiry.ForUpdate(record, 10, null));
    }

    [Fact]
    public void ForUpdate_ABasisWithoutADeadline_IsValidatedButNotStored()
    {
        var record = Record(SessionMode.Interactive, null, null);

        Assert.Equal((null, null), SessionExpiry.ForUpdate(record, null, "start"));
        Assert.Throws<ArgumentException>(() => SessionExpiry.ForUpdate(record, null, "whenever"));
    }

    [Fact]
    public void UpdateValidator_LetsAScheduledSessionExpireFromStart_ButNotFromActivity()
    {
        var scheduled = Record(SessionMode.Scheduled, null, null);

        // Not a runtime field: a scheduled session may take it without recreating its CronJob.
        SessionUpdateValidator.Validate(scheduled, new UpdateSessionRequest { AutoDeleteAfterSeconds = 3600 });
        SessionUpdateValidator.Validate(scheduled, new UpdateSessionRequest { AutoDeleteAfterSeconds = 3600, AutoDeleteFrom = "start" });
        Assert.Throws<ArgumentException>(() => SessionUpdateValidator.Validate(scheduled,
            new UpdateSessionRequest { AutoDeleteAfterSeconds = 3600, AutoDeleteFrom = "lastActivity" }));
    }

    [Fact]
    public void ExpiresAt_CountsFromTheChosenBasis_AndIsNullWhenOff()
    {
        var created = new DateTime(2026, 10, 10, 8, 0, 0, DateTimeKind.Utc);
        var touched = created.AddHours(5);
        var fromStart = Record(SessionMode.Interactive, 3600, "start", created, touched);
        var fromActivity = Record(SessionMode.Interactive, 3600, "lastActivity", created, touched);
        var off = Record(SessionMode.Interactive, null, null, created, touched);

        Assert.Equal(created.AddHours(1), SessionExpiry.ExpiresAt(fromStart));
        Assert.Equal(touched.AddHours(1), SessionExpiry.ExpiresAt(fromActivity));
        Assert.Null(SessionExpiry.ExpiresAt(off));

        Assert.True(SessionExpiry.IsDue(fromStart, touched));
        Assert.False(SessionExpiry.IsDue(fromActivity, touched.AddMinutes(59)));
        Assert.True(SessionExpiry.IsDue(fromActivity, touched.AddMinutes(60)));
        Assert.False(SessionExpiry.IsDue(off, DateTime.MaxValue));
    }

    [Fact]
    public void Duplicate_CopiesTheSettingButNotTheTimestamps()
    {
        var source = Record(SessionMode.Autonomous, 86400, "start",
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));

        var copy = SessionDuplication.CopyableRequest(source, new DuplicateSessionRequest("copy", null, false));
        Assert.Equal(86400, copy.AutoDeleteAfterSeconds);
        Assert.Equal("start", copy.AutoDeleteFrom);

        // The create request carries no timestamps at all, so the copy counts from its own start.
        var cleared = SessionDuplication.CopyableRequest(source,
            new DuplicateSessionRequest("copy", null, false, AutoDeleteAfterSeconds: 0));
        Assert.Equal((null, null), SessionExpiry.ForCreate(cleared.AutoDeleteAfterSeconds, cleared.AutoDeleteFrom, cleared.Mode));

        var replaced = SessionDuplication.CopyableRequest(source,
            new DuplicateSessionRequest("copy", null, false, AutoDeleteAfterSeconds: 600, AutoDeleteFrom: "lastActivity"));
        Assert.Equal((600, "lastActivity"), (replaced.AutoDeleteAfterSeconds, replaced.AutoDeleteFrom));
    }

    private static SessionRecord Record(SessionMode mode, int? seconds, string? from,
        DateTime? createdAt = null, DateTime? lastActivityAt = null) => new()
    {
        Id = "s-1", Owner = "alice", Mode = mode,
        AgentSessionId = "agent", CallbackToken = "token",
        AutoDeleteAfterSeconds = seconds, AutoDeleteFrom = from,
        CreatedAt = createdAt ?? DateTime.UtcNow,
        LastActivityAt = lastActivityAt ?? createdAt ?? DateTime.UtcNow
    };
}
