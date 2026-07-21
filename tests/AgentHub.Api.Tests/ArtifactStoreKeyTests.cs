using AgentHub.Api.Models;
using AgentHub.Api.Storage;
using Xunit;

namespace AgentHub.Api.Tests;

public class ArtifactStoreKeyTests
{
    [Fact]
    public void StateKey_ClaudeKeepsExactLegacyKey()
    {
        Assert.Equal(
            "sessions/alice/session-id/claude-state.tgz",
            IArtifactStore.StateKey("alice", "session-id", AgentKind.Claude));
        Assert.Equal(
            IArtifactStore.StateKey("alice", "session-id"),
            IArtifactStore.StateKey("alice", "session-id", AgentKind.Claude));
    }

    [Fact]
    public void StateKey_CodexUsesSeparateProviderKey()
    {
        Assert.Equal(
            "sessions/alice/session-id/codex-state.tgz",
            IArtifactStore.StateKey("alice", "session-id", AgentKind.Codex));
        Assert.NotEqual(
            IArtifactStore.StateKey("alice", "session-id", AgentKind.Claude),
            IArtifactStore.StateKey("alice", "session-id", AgentKind.Codex));
    }

    [Fact]
    public void ProviderStateKeys_DoNotChangeOtherArtifactKeys()
    {
        Assert.Equal("sessions/alice/session-id/scrollback.log",
            IArtifactStore.ScrollbackKey("alice", "session-id"));
        Assert.Equal("sessions/alice/session-id/artifacts/report.txt",
            IArtifactStore.ArtifactKey("alice", "session-id", "/report.txt"));
    }
}
