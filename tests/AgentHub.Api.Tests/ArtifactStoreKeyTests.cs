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
    public void StateKey_CursorUsesSeparateProviderKey()
    {
        Assert.Equal(
            "sessions/alice/session-id/cursor-state.tgz",
            IArtifactStore.StateKey("alice", "session-id", AgentKind.Cursor));
        Assert.NotEqual(
            IArtifactStore.StateKey("alice", "session-id", AgentKind.Codex),
            IArtifactStore.StateKey("alice", "session-id", AgentKind.Cursor));
    }

    [Fact]
    public void StateKey_OpenClawUsesSeparateProviderKey()
    {
        Assert.Equal(
            "sessions/alice/session-id/openclaw-state.tgz",
            IArtifactStore.StateKey("alice", "session-id", AgentKind.OpenClaw));
        Assert.NotEqual(
            IArtifactStore.StateKey("alice", "session-id", AgentKind.Cursor),
            IArtifactStore.StateKey("alice", "session-id", AgentKind.OpenClaw));
    }

    [Fact]
    public void ProviderStateKeys_DoNotChangeOtherArtifactKeys()
    {
        Assert.Equal("sessions/alice/session-id/scrollback.log",
            IArtifactStore.ScrollbackKey("alice", "session-id"));
        Assert.Equal("sessions/alice/session-id/artifacts/report.txt",
            IArtifactStore.ArtifactKey("alice", "session-id", "/report.txt"));
        Assert.Equal("sessions/alice/session-id/browser-cookies.json",
            IArtifactStore.BrowserCookiesKey("alice", "session-id"));
    }

    [Fact]
    public void SessionFileKey_encodes_untrusted_segments_without_path_traversal()
    {
        var key = IArtifactStore.SessionFileKey("alice/team", "../session", "file-id", "shot.png");

        Assert.Equal(
            "sessions/alice%2Fteam/..%2Fsession/files/file-id/shot.png",
            key);
    }
}
