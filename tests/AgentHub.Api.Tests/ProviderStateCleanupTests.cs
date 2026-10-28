using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using AgentHub.Api.Models;
using AgentHub.Api.Services;
using Xunit;

namespace AgentHub.Api.Tests;

public class ProviderStateCleanupTests
{
    [Theory]
    [InlineData(AgentKind.Claude, ".claude/.credentials.json")]
    [InlineData(AgentKind.Codex, ".codex/auth.json")]
    [InlineData(AgentKind.Cursor, ".cursor/auth.json")]
    [InlineData(AgentKind.OpenCode, ".opencode/auth.json")]
    [InlineData(AgentKind.OpenCode, ".opencode/mcp-auth.json")]
    [InlineData(AgentKind.OpenClaw, ".openclaw/auth-profiles.json")]
    [InlineData(AgentKind.OpenClaw, ".openclaw/agents/main/agent/auth-profiles.json")]
    [InlineData(AgentKind.OpenClaw, ".openclaw/agents/another/agent/openclaw-agent.sqlite")]
    [InlineData(AgentKind.OpenClaw, ".openclaw/agents/another/agent/openclaw-agent.sqlite-wal")]
    [InlineData(AgentKind.OpenClaw, ".openclaw/agents/another/agent/openclaw-agent.sqlite-shm")]
    [InlineData(AgentKind.Claude, ".claude/..credentials.json.123.tmp")]
    [InlineData(AgentKind.Codex, ".codex/.auth.json.123.tmp")]
    [InlineData(AgentKind.Cursor, ".cursor/.auth.json.123.tmp")]
    [InlineData(AgentKind.OpenCode, ".opencode/.auth.json.123.tmp")]
    [InlineData(AgentKind.OpenCode, ".opencode/.mcp-auth.json.123.tmp")]
    [InlineData(AgentKind.OpenClaw, ".openclaw/.auth-profiles.json.123.tmp")]
    [InlineData(AgentKind.OpenClaw, ".openclaw/agents/main/agent/.auth-profiles.json.123.tmp")]
    public async Task RemovesOnlyKnownLoginEntries(AgentKind agent, string login)
    {
        using var input = new MemoryStream();
        using (var gzip = new GZipStream(input, CompressionLevel.Fastest, true))
        using (var writer = new TarWriter(gzip, TarEntryFormat.Gnu, true))
            foreach (var name in new[] { "./" + login, login + ".notes", "history/auth.json", ".openclaw/agents/main/sessions/auth-profiles.json" })
                writer.WriteEntry(new GnuTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(name)) });
        input.Position = 0;
        using var output = await ClaudeStateCleanup.RemoveCredentialsAsync(input, agent, default)
            ?? throw new Xunit.Sdk.XunitException("Expected login removal.");
        using var decompressed = new GZipStream(output, CompressionMode.Decompress, true);
        using var reader = new TarReader(decompressed);
        var names = new List<string>();
        while (reader.GetNextEntry() is { } entry)
        {
            names.Add(entry.Name);
            using var data = new StreamReader(entry.DataStream ?? throw new Xunit.Sdk.XunitException("Retained payload missing."));
            Assert.Equal(entry.Name, await data.ReadToEndAsync());
        }
        Assert.Equal([login + ".notes", "history/auth.json", ".openclaw/agents/main/sessions/auth-profiles.json"], names);
        output.Position = 0;
        Assert.Null(await ClaudeStateCleanup.RemoveCredentialsAsync(output, agent, default));
    }

    [Theory]
    [InlineData(AgentKind.Claude, ".codex/auth.json")]
    [InlineData(AgentKind.Codex, ".claude/.credentials.json")]
    [InlineData(AgentKind.Cursor, ".cursor/projects/auth.json")]
    [InlineData(AgentKind.OpenCode, ".opencode/projects/mcp-auth.json")]
    [InlineData(AgentKind.OpenClaw, ".openclaw/agents/main/sessions/auth-profiles.json")]
    [InlineData(AgentKind.OpenClaw, ".openclaw/agents/main/agent/openclaw-agent.sqlite.backup")]
    [InlineData(AgentKind.Codex, ".codex/.auth.json.notes.tmp")]
    [InlineData(AgentKind.Codex, ".codex/.auth.json.0.tmp")]
    [InlineData(AgentKind.Codex, ".codex/.auth.json.012.tmp")]
    [InlineData(AgentKind.Codex, ".codex/.auth.json.-12.tmp")]
    [InlineData(AgentKind.Codex, ".codex/.history.123.tmp")]
    [InlineData(AgentKind.Codex, ".cursor/.auth.json.123.tmp")]
    [InlineData(AgentKind.OpenClaw, ".openclaw/agents/main/sessions/.auth-profiles.json.123.tmp")]
    public void KeepsUnrelatedNamesAndOtherProviders(AgentKind agent, string path)
        => Assert.False(StateArchiveCredentials.Contains(agent, path));
}
