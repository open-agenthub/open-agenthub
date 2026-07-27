using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using Xunit;

namespace AgentHub.Api.Tests;

public class SessionParentTests
{
    [Fact]
    public void CreateSessionRequest_AcceptsParentSessionId()
    {
        var req = new CreateSessionRequest
        {
            Title = "child",
            Mode = SessionMode.Autonomous,
            Prompt = "do work",
            ParentSessionId = "parent-1"
        };
        Assert.Equal("parent-1", req.ParentSessionId);
    }

    [Fact]
    public void SessionRecordAndInfo_ExposeParentSessionId()
    {
        var record = new SessionRecord
        {
            Id = "child-1",
            Owner = "owner-1",
            CallbackToken = "tok",
            ParentSessionId = "parent-1"
        };
        Assert.Equal("parent-1", record.ParentSessionId);

        var info = new SessionInfo
        {
            Id = record.Id,
            Title = "child",
            Owner = record.Owner,
            Mode = SessionMode.Autonomous,
            Phase = "Pending",
            ParentSessionId = record.ParentSessionId
        };
        Assert.Equal("parent-1", info.ParentSessionId);
    }
}
