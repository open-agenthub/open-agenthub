using AgentHub.Api.Licensing;
using Xunit;

namespace AgentHub.Api.Tests;

public class SeatUsageReporterTests
{
    [Fact]
    public void TryParseRenewedToken_ReturnsTheRenewedToken()
    {
        var json = """{"received":true,"renewed":true,"seats":7,"token":"eyJ.header.sig","validUntil":"2026-09-01T00:00:00Z"}""";
        Assert.True(SeatUsageReporter.TryParseRenewedToken(json, out var token));
        Assert.Equal("eyJ.header.sig", token);
    }

    [Fact]
    public void TryParseRenewedToken_FalseWhenNoTokenField()
    {
        // 403 body from a lapsed subscription — accepted but not renewed.
        var json = """{"received":true,"renewed":false,"error":"License is not active."}""";
        Assert.False(SeatUsageReporter.TryParseRenewedToken(json, out var token));
        Assert.Equal("", token);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("{\"token\":\"\"}")]
    [InlineData("{\"token\":123}")]
    public void TryParseRenewedToken_FalseOnBadOrEmpty(string json)
        => Assert.False(SeatUsageReporter.TryParseRenewedToken(json, out _));
}
