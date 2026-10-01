using System.Text.Json;
using System.Text.Json.Nodes;
using AgentHub.Api.Ee.Library;
using AgentHub.Api.Library;
using AgentHub.Api.Persistence;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentHub.Api.Tests;

public class SkillLibraryMcpServiceTests
{
    private const string HubUrl = "https://hub.example.com";

    private static (SkillLibraryMcpService Service, InMemorySkillStore Skills, InMemoryLibraryShareStore Shares)
        Build(bool licensed = true)
    {
        var skills = new InMemorySkillStore();
        var shares = new InMemoryLibraryShareStore();
        var access = new LibraryAccessService(
            new InMemoryMcpServerStore(), skills, shares, new FakeEnterpriseLicense(licensed));
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AgentHub:CallbackBaseUrl"] = HubUrl })
            .Build();
        var service = new SkillLibraryMcpService(access, skills, LibraryTest.SearchService(skills), cfg);
        return (service, skills, shares);
    }

    private static SessionRecord Session(string owner = "alice", string? projectId = null) => new()
    {
        Id = "s1",
        Owner = owner,
        Title = "t",
        ProjectId = projectId,
        AgentSessionId = "agent-1",
        CallbackToken = "token"
    };

    private static async Task<JsonObject?> CallAsync(SkillLibraryMcpService service, SessionRecord session, string json)
    {
        using var doc = JsonDocument.Parse(json);
        return await service.HandleAsync(doc.RootElement, session, CancellationToken.None);
    }

    /// <summary>The tool result text — plain text, not JSON.</summary>
    private static string ToolText(JsonObject? response) =>
        response!["result"]!["content"]![0]!["text"]!.GetValue<string>();

    private static bool IsError(JsonObject? response) =>
        response!["result"]!["isError"]?.GetValue<bool>() ?? false;

    [Fact]
    public async Task Initialize_EchoesKnownProtocol_AndAdvertisesTools()
    {
        var (service, _, _) = Build();
        var response = await CallAsync(service, Session(),
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26"}}""");

        Assert.Equal("2025-03-26", response!["result"]!["protocolVersion"]!.GetValue<string>());
        Assert.NotNull(response["result"]!["capabilities"]!["tools"]);

        var tools = await CallAsync(service, Session(),
            """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
        var names = tools!["result"]!["tools"]!.AsArray()
            .Select(t => t!["name"]!.GetValue<string>()).ToList();
        Assert.Equal(
            ["search_skills", "get_skill", "upload_skill", "list_skill_versions", "restore_skill_version"],
            names);
    }

    [Fact]
    public async Task Notifications_ProduceNoResponse_AndUnknownMethodsAnError()
    {
        var (service, _, _) = Build();
        Assert.Null(await CallAsync(service, Session(),
            """{"jsonrpc":"2.0","method":"notifications/initialized"}"""));

        var error = await CallAsync(service, Session(),
            """{"jsonrpc":"2.0","id":3,"method":"resources/list"}""");
        Assert.Equal(-32601, error!["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task UploadSkill_CreatesInSessionProjectScope_AndVersionsOnUpdate()
    {
        var (service, skills, _) = Build();
        var session = Session(projectId: "project-1");

        var created = ToolText(await CallAsync(service, session,
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"upload_skill","arguments":{"name":"deploy","description":"Deploy helper","content":"# v1"}}}"""));
        Assert.Contains("created", created);
        Assert.Contains("project library", created);
        Assert.Contains("v1", created);

        var updated = ToolText(await CallAsync(service, session,
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"upload_skill","arguments":{"name":"deploy","content":"# v2","comment":"tweak"}}}"""));
        Assert.Contains("updated", updated);
        Assert.Contains("v2", updated);

        var record = (await skills.ListByOwnerAsync("alice")).Single();
        Assert.Equal("project-1", record.ProjectId);
        // The description survives an update that omits it.
        Assert.Equal("Deploy helper", record.Description);
        Assert.Equal("# v2", await skills.GetContentAsync(record));
    }

    [Fact]
    public async Task UploadSkill_WithoutProject_LandsInPersonalLibrary()
    {
        var (service, skills, _) = Build();
        var text = ToolText(await CallAsync(service, Session(),
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"upload_skill","arguments":{"name":"notes","content":"# n"}}}"""));
        Assert.Contains("personal library", text);
        Assert.Null((await skills.ListByOwnerAsync("alice")).Single().ProjectId);
    }

    [Fact]
    public async Task SearchAndGet_CoverProjectPersonalAndSharedScopes()
    {
        var (service, skills, shares) = Build();
        skills.Add("alice", "own-notes", "# personal kubernetes notes");
        skills.Add("alice", "project-notes", "# project kubernetes runbook", projectId: "project-1");
        skills.Add("alice", "other-project", "# hidden", projectId: "project-2");
        var foreign = skills.Add("bob", "shared-notes", "# shared kubernetes guide");
        await shares.SetSharesAsync(LibraryItemTypes.Skill, foreign.Id, all: true, null, null, "bob");

        var session = Session(projectId: "project-1");
        var results = ToolText(await CallAsync(service, session,
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search_skills","arguments":{"query":"kubernetes"}}}"""));

        Assert.Contains("3 skill(s) match", results);
        Assert.Contains("own-notes (v1, personal", results);
        Assert.Contains("project-notes (v1, project", results);
        Assert.Contains("shared-notes (v1, shared", results);
        Assert.DoesNotContain("other-project", results);

        var detail = ToolText(await CallAsync(service, session,
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"get_skill","arguments":{"name":"shared-notes"}}}"""));
        Assert.Contains("# shared kubernetes guide", detail);
        Assert.Contains("SKILL.md:", detail);
    }

    [Fact]
    public async Task Search_WithoutHits_SaysSoAndInvitesAnUpload()
    {
        var (service, _, _) = Build();
        var text = ToolText(await CallAsync(service, Session(),
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search_skills","arguments":{"query":"nothing here"}}}"""));
        Assert.Contains("No skill matches", text);
        Assert.Contains("upload_skill", text);
    }

    [Fact]
    public async Task VersionsAndRestore_WorkForOwnSkills_ButRestoreRejectsShared()
    {
        var (service, skills, shares) = Build();
        var session = Session();
        var record = skills.Add("alice", "deploy", "# v1");
        await skills.UpdateAsync("alice", record.Id, new SaveSkillRequest("deploy", null, "# v2"));

        var versions = ToolText(await CallAsync(service, session,
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"list_skill_versions","arguments":{"name":"deploy"}}}"""));
        Assert.Contains("latest v2, 2 version(s)", versions);

        var restored = ToolText(await CallAsync(service, session,
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"restore_skill_version","arguments":{"name":"deploy","version":1}}}"""));
        Assert.Contains("Restored deploy v1 as the new v3", restored);
        Assert.Equal("# v1", await skills.GetContentAsync((await skills.GetManyAsync([record.Id])).Single()));

        var foreign = skills.Add("bob", "borrowed", "# b");
        await shares.SetSharesAsync(LibraryItemTypes.Skill, foreign.Id, all: true, null, null, "bob");
        var denied = await CallAsync(service, session,
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"restore_skill_version","arguments":{"name":"borrowed","version":1}}}""");
        Assert.True(IsError(denied));
    }

    [Fact]
    public async Task UploadSkill_WithScripts_StoresAndListsAndReadsThem()
    {
        var (service, skills, _) = Build();
        var session = Session();

        var uploaded = ToolText(await CallAsync(service, session,
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"upload_skill","arguments":{"name":"deploy","content":"# skill","files":[{"path":"scripts/check.sh","content":"#!/bin/sh\ntrue"},{"path":"reference.md","content":"# docs"}]}}}"""));
        Assert.Contains("extra files (2): scripts/check.sh, reference.md", uploaded);

        var detail = ToolText(await CallAsync(service, session,
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"get_skill","arguments":{"name":"deploy"}}}"""));
        Assert.Contains("extra files (2)", detail);
        // The content of an extra file never shows up in a plain get_skill.
        Assert.DoesNotContain("#!/bin/sh", detail);

        var file = ToolText(await CallAsync(service, session,
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"get_skill","arguments":{"name":"deploy","file":"scripts/check.sh"}}}"""));
        Assert.Contains("#!/bin/sh\ntrue", file);

        // Update without files keeps them; missing file paths list the alternatives.
        ToolText(await CallAsync(service, session,
            """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"upload_skill","arguments":{"name":"deploy","content":"# v2"}}}"""));
        var record = (await skills.ListByOwnerAsync("alice")).Single();
        Assert.Equal(2, (await skills.GetFilesAsync(record.Id, record.Version)).Count);

        var missing = await CallAsync(service, session,
            """{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"get_skill","arguments":{"name":"deploy","file":"nope.sh"}}}""");
        Assert.True(IsError(missing));
        Assert.Contains("scripts/check.sh", ToolText(missing));
    }

    [Fact]
    public async Task GetSkill_OffersCurlDownloadsInsteadOfInliningFiles()
    {
        var (service, skills, _) = Build();
        skills.Add("alice", "deploy", "# skill", files:
            [new SkillFile("scripts/check.sh", "#!/bin/sh\ntrue"), new SkillFile("reference.md", "# docs")]);

        var detail = ToolText(await CallAsync(service, Session(),
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"get_skill","arguments":{"name":"deploy"}}}"""));

        Assert.Contains("X-Agent-Token: $AGENTHUB_CALLBACK_TOKEN", detail);
        Assert.Contains($"{HubUrl}/internal/sessions/s1/skills/skill-1/files.tar.gz?version=1", detail);
        Assert.Contains("tar xzf", detail);
    }

    [Fact]
    public async Task GetSkill_WithASingleFile_PointsStraightAtIt()
    {
        var (service, skills, _) = Build();
        skills.Add("alice", "deploy", "# skill", files: [new SkillFile("scripts/check.sh", "x")]);

        var detail = ToolText(await CallAsync(service, Session(),
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"get_skill","arguments":{"name":"deploy"}}}"""));

        Assert.Contains(
            $"{HubUrl}/internal/sessions/s1/skills/skill-1/files/scripts/check.sh?version=1", detail);
    }

    [Fact]
    public async Task GetSkill_WithoutFiles_MentionsNoDownload()
    {
        var (service, skills, _) = Build();
        skills.Add("alice", "notes", "# just text");

        var detail = ToolText(await CallAsync(service, Session(),
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"get_skill","arguments":{"name":"notes"}}}"""));

        Assert.DoesNotContain("curl", detail);
        Assert.Contains("# just text", detail);
    }

    [Fact]
    public async Task Results_ArePlainText_WithoutEscapedUmlautsOrEmoji()
    {
        // The reason the results are text at all: JSON-encoding "ö" costs six tokens
        // instead of one, and a German runbook is full of them.
        var (service, skills, _) = Build();
        skills.Add("alice", "größe", "# Größe prüfen 🚀", projectId: null);

        var detail = ToolText(await CallAsync(service, Session(),
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"get_skill","arguments":{"name":"größe"}}}"""));
        Assert.Contains("# Größe prüfen 🚀", detail);

        // Not JSON — parsing it as such has to fail, or the escapes are back.
        Assert.ThrowsAny<JsonException>(() => JsonNode.Parse(detail));
    }

    [Fact]
    public async Task UploadSkill_RejectsUnsafeFilePaths()
    {
        var (service, _, _) = Build();
        var response = await CallAsync(service, Session(),
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"upload_skill","arguments":{"name":"bad","content":"# x","files":[{"path":"../escape.sh","content":"x"}]}}}""");
        Assert.True(IsError(response));
    }

    [Fact]
    public async Task RuntimeOnlyArguments_FailLoudlyWhenNoProxyIsInFront()
    {
        // Silently ignoring them is the dangerous outcome: the agent reports files as
        // written to disk that were never written.
        var (service, skills, _) = Build();
        skills.Add("alice", "deploy", "# skill", files: [new SkillFile("scripts/check.sh", "x")]);

        var upload = await CallAsync(service, Session(),
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"upload_skill","arguments":{"name":"deploy","content":"# x","path":"/workspace/skill"}}}""");
        Assert.True(IsError(upload));
        Assert.Contains("session runtime", ToolText(upload));

        var get = await CallAsync(service, Session(),
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"get_skill","arguments":{"name":"deploy","out_dir":"/tmp/x"}}}""");
        Assert.True(IsError(get));
        Assert.Contains("session runtime", ToolText(get));
    }

    [Fact]
    public async Task Initialize_InstructionsNudgeProactiveUse()
    {
        var (service, _, _) = Build();
        var response = await CallAsync(service, Session(),
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""");
        var instructions = response!["result"]!["instructions"]!.GetValue<string>();
        Assert.Contains("proactively", instructions);
        Assert.Contains("upload_skill", instructions);
        Assert.Contains("search_skills", instructions);
        // A large script was the reason uploads were skipped; the instructions say it is not one.
        Assert.Contains("never have to travel through your context", instructions);
    }

    [Fact]
    public async Task ToolErrors_AreReportedAsToolResults()
    {
        var (service, _, _) = Build();
        var response = await CallAsync(service, Session(),
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"get_skill","arguments":{"name":"missing"}}}""");
        Assert.True(IsError(response));
        Assert.Contains("missing", ToolText(response));
    }
}
