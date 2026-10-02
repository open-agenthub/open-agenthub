using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using AgentHub.Api.Controllers;
using AgentHub.Api.Ee.Library;
using AgentHub.Api.Library;
using AgentHub.Api.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>
/// The routes that let a skill's files reach the agent's disk without their content
/// passing through its context: one file, or the whole skill as a tar.gz. Both read
/// through ISkillStore, so they also work on an instance without object storage.
/// </summary>
public class SkillDownloadRoutesTests
{
    private const string Token = "callback-token";

    private static (SkillLibraryMcpController Controller, InMemorySkillStore Skills) Build(
        string? token = Token, string? projectId = null)
    {
        var skills = new InMemorySkillStore();
        var shares = new InMemoryLibraryShareStore();
        var access = new LibraryAccessService(
            new InMemoryMcpServerStore(), skills, shares, new FakeEnterpriseLicense(true));
        var search = LibraryTest.SearchService(skills);
        var sessions = new SingleSessionStore(new SessionRecord
        {
            Id = "s1",
            Owner = "alice",
            Title = "t",
            ProjectId = projectId,
            AgentSessionId = "agent-1",
            CallbackToken = Token
        });
        var controller = new SkillLibraryMcpController(
            sessions,
            new SkillLibraryMcpService(access, skills, search),
            new SkillImporter(skills, search, NullLogger<SkillImporter>.Instance),
            skills)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        if (token is not null)
            controller.Request.Headers["X-Agent-Token"] = token;
        return (controller, skills);
    }

    private static async Task<byte[]> BytesAsync(IActionResult result)
    {
        var file = Assert.IsType<FileContentResult>(result);
        return await Task.FromResult(file.FileContents);
    }

    [Fact]
    public async Task One_file_is_served_as_its_own_bytes()
    {
        var (controller, skills) = Build();
        skills.Add("alice", "deploy", "# skill",
            files: [new SkillFile("scripts/check.sh", "#!/bin/sh\necho größe 🚀")]);

        var result = await controller.SkillFile("s1", "deploy", "scripts/check.sh", null, default);

        Assert.Equal("#!/bin/sh\necho größe 🚀", Encoding.UTF8.GetString(await BytesAsync(result)));
        Assert.Equal("check.sh", Assert.IsType<FileContentResult>(result).FileDownloadName);
    }

    [Fact]
    public async Task SKILL_md_is_downloadable_too()
    {
        var (controller, skills) = Build();
        skills.Add("alice", "deploy", "# the skill itself");

        var result = await controller.SkillFile("s1", "deploy", "SKILL.md", null, default);

        Assert.Equal("# the skill itself", Encoding.UTF8.GetString(await BytesAsync(result)));
    }

    [Fact]
    public async Task A_skill_id_resolves_as_well_as_its_name()
    {
        var (controller, skills) = Build();
        var record = skills.Add("alice", "deploy", "# skill", files: [new SkillFile("a.md", "A")]);

        var result = await controller.SkillFile("s1", record.Id, "a.md", null, default);

        Assert.Equal("A", Encoding.UTF8.GetString(await BytesAsync(result)));
    }

    [Fact]
    public async Task An_older_version_is_served_when_asked_for()
    {
        var (controller, skills) = Build();
        var record = skills.Add("alice", "deploy", "# v1", files: [new SkillFile("a.md", "first")]);
        await skills.UpdateAsync("alice", record.Id,
            new SaveSkillRequest("deploy", null, "# v2", Files: [new SkillFile("a.md", "second")]));

        Assert.Equal("first", Encoding.UTF8.GetString(
            await BytesAsync(await controller.SkillFile("s1", "deploy", "a.md", 1, default))));
        Assert.Equal("second", Encoding.UTF8.GetString(
            await BytesAsync(await controller.SkillFile("s1", "deploy", "a.md", null, default))));
    }

    [Fact]
    public async Task The_bundle_is_a_usable_skill_directory()
    {
        var (controller, skills) = Build();
        skills.Add("alice", "deploy", "# skill\n\nGröße prüfen 🚀", files:
        [
            new SkillFile("scripts/check.sh", "#!/bin/sh\ntrue"),
            new SkillFile("reference.md", "# docs")
        ]);

        var result = await controller.SkillBundle("s1", "deploy", null, default);
        var entries = await ReadTarGzAsync(await BytesAsync(result));

        Assert.Equal(["SKILL.md", "scripts/check.sh", "reference.md"], entries.Keys);
        Assert.Equal("# skill\n\nGröße prüfen 🚀", entries["SKILL.md"].Content);
        // A helper that arrives without +x fails on the first run with an error that
        // says nothing about the missing bit.
        Assert.True(entries["scripts/check.sh"].Mode.HasFlag(UnixFileMode.UserExecute));
        Assert.False(entries["reference.md"].Mode.HasFlag(UnixFileMode.UserExecute));
        Assert.Equal("deploy-v1.tar.gz", Assert.IsType<FileContentResult>(result).FileDownloadName);
    }

    [Fact]
    public async Task A_foreign_token_reaches_nothing()
    {
        var (controller, skills) = Build(token: "someone-elses-token");
        skills.Add("alice", "deploy", "# skill", files: [new SkillFile("a.md", "A")]);

        Assert.IsType<UnauthorizedResult>(
            await controller.SkillFile("s1", "deploy", "a.md", null, default));
        Assert.IsType<UnauthorizedResult>(
            await controller.SkillBundle("s1", "deploy", null, default));
    }

    [Fact]
    public async Task A_missing_token_reaches_nothing()
    {
        var (controller, skills) = Build(token: null);
        skills.Add("alice", "deploy", "# skill");

        Assert.IsType<UnauthorizedResult>(
            await controller.SkillBundle("s1", "deploy", null, default));
    }

    [Fact]
    public async Task A_skill_of_another_owner_is_not_readable_without_a_share()
    {
        var (controller, skills) = Build();
        skills.Add("bob", "private-notes", "# secret", files: [new SkillFile("a.md", "A")]);

        var result = await controller.SkillFile("s1", "private-notes", "a.md", null, default);

        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        Assert.Contains("private-notes", notFound.Value!.ToString());
    }

    [Fact]
    public async Task An_unknown_file_lists_what_the_skill_has()
    {
        var (controller, skills) = Build();
        skills.Add("alice", "deploy", "# skill", files: [new SkillFile("a.md", "A")]);

        var result = await controller.SkillFile("s1", "deploy", "nope.sh", null, default);

        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        Assert.Contains("a.md", System.Text.Json.JsonSerializer.Serialize(notFound.Value));
    }

    /// <summary>The route the MCP result hands the agent has to be the route that serves it.</summary>
    [Fact]
    public async Task The_advertised_route_matches_what_the_controller_serves()
    {
        var (controller, skills) = Build();
        var record = skills.Add("alice", "deploy", "# skill", files: [new SkillFile("scripts/check.sh", "A")]);

        var route = SkillLibraryMcpService.FileRoute("s1", record.Id, "scripts/check.sh", 1);
        Assert.Equal($"/internal/sessions/s1/skills/{record.Id}/files/scripts/check.sh?version=1", route);
        Assert.Equal($"/internal/sessions/s1/skills/{record.Id}/files.tar.gz?version=1",
            SkillLibraryMcpService.BundleRoute("s1", record.Id, 1));

        // And the handler behind it answers.
        Assert.Equal("A", Encoding.UTF8.GetString(
            await BytesAsync(await controller.SkillFile("s1", record.Id, "scripts/check.sh", 1, default))));
    }

    private static async Task<Dictionary<string, (string Content, UnixFileMode Mode)>> ReadTarGzAsync(
        byte[] bytes)
    {
        var entries = new Dictionary<string, (string, UnixFileMode)>();
        await using var gzip = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
        await using var tar = new TarReader(gzip);
        while (await tar.GetNextEntryAsync() is { } entry)
        {
            using var reader = new StreamReader(entry.DataStream!);
            entries[entry.Name] = (await reader.ReadToEndAsync(), entry.Mode);
        }
        return entries;
    }

    private sealed class SingleSessionStore(SessionRecord session) : ISessionStore
    {
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task UpsertAsync(SessionRecord r, CancellationToken ct = default) => Task.CompletedTask;
        public Task<SessionRecord?> GetAsync(string owner, string id, CancellationToken ct = default) =>
            Task.FromResult(owner == session.Owner && id == session.Id ? session : null);
        public Task<SessionRecord?> GetByIdAsync(string id, CancellationToken ct = default) =>
            Task.FromResult(id == session.Id ? session : null);
        public Task<SessionRecord?> GetByCallbackTokenAsync(string token, CancellationToken ct = default) =>
            Task.FromResult(token == session.CallbackToken ? session : null);
        public Task<IReadOnlyList<SessionRecord>> ListAsync(string owner, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SessionRecord>>(owner == session.Owner ? [session] : []);
        public Task UpdateStatusAsync(string id, string status, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetQuestionPendingAsync(string id, bool pending, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task SetScrollbackAsync(string id, string text, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetScrollbackAsync(string id, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);
        public Task DeleteAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
    }
}
