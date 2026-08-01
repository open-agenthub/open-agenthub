using System.Text.Json;
using System.Text.Json.Nodes;
using AgentHub.Api.Ee.Library;
using AgentHub.Api.Library;
using AgentHub.Api.Persistence;
using Xunit;

namespace AgentHub.Api.Tests;

public class SkillLibraryMcpServiceTests
{
    private static (SkillLibraryMcpService Service, InMemorySkillStore Skills, InMemoryLibraryShareStore Shares)
        Build(bool licensed = true)
    {
        var skills = new InMemorySkillStore();
        var shares = new InMemoryLibraryShareStore();
        var access = new LibraryAccessService(
            new InMemoryMcpServerStore(), skills, shares, new FakeEnterpriseLicense(licensed));
        var service = new SkillLibraryMcpService(access, skills, LibraryTest.SearchService(skills));
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

    private static JsonObject ToolPayload(JsonObject? response)
    {
        var text = response!["result"]!["content"]![0]!["text"]!.GetValue<string>();
        return JsonNode.Parse(text)!.AsObject();
    }

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

        var created = ToolPayload(await CallAsync(service, session,
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"upload_skill","arguments":{"name":"deploy","description":"Deploy helper","content":"# v1"}}}"""));
        Assert.True(created["created"]!.GetValue<bool>());
        Assert.Equal("project", created["scope"]!.GetValue<string>());
        Assert.Equal(1, created["version"]!.GetValue<int>());

        var updated = ToolPayload(await CallAsync(service, session,
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"upload_skill","arguments":{"name":"deploy","content":"# v2","comment":"tweak"}}}"""));
        Assert.False(updated["created"]!.GetValue<bool>());
        Assert.Equal(2, updated["version"]!.GetValue<int>());

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
        ToolPayload(await CallAsync(service, Session(),
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"upload_skill","arguments":{"name":"notes","content":"# n"}}}"""));
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
        var results = ToolPayload(await CallAsync(service, session,
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search_skills","arguments":{"query":"kubernetes"}}}"""));

        var scopes = results["results"]!.AsArray()
            .ToDictionary(r => r!["name"]!.GetValue<string>(), r => r!["scope"]!.GetValue<string>());
        Assert.Equal(3, results["total"]!.GetValue<int>());
        Assert.Equal("personal", scopes["own-notes"]);
        Assert.Equal("project", scopes["project-notes"]);
        Assert.Equal("shared", scopes["shared-notes"]);
        Assert.DoesNotContain("other-project", scopes.Keys);

        var detail = ToolPayload(await CallAsync(service, session,
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"get_skill","arguments":{"name":"shared-notes"}}}"""));
        Assert.Equal("# shared kubernetes guide", detail["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task VersionsAndRestore_WorkForOwnSkills_ButRestoreRejectsShared()
    {
        var (service, skills, shares) = Build();
        var session = Session();
        var record = skills.Add("alice", "deploy", "# v1");
        await skills.UpdateAsync("alice", record.Id, new SaveSkillRequest("deploy", null, "# v2"));

        var versions = ToolPayload(await CallAsync(service, session,
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"list_skill_versions","arguments":{"name":"deploy"}}}"""));
        Assert.Equal(2, versions["latest"]!.GetValue<int>());
        Assert.Equal(2, versions["versions"]!.AsArray().Count);

        var restored = ToolPayload(await CallAsync(service, session,
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"restore_skill_version","arguments":{"name":"deploy","version":1}}}"""));
        Assert.Equal(3, restored["version"]!.GetValue<int>());
        Assert.Equal("# v1", await skills.GetContentAsync((await skills.GetManyAsync([record.Id])).Single()));

        var foreign = skills.Add("bob", "borrowed", "# b");
        await shares.SetSharesAsync(LibraryItemTypes.Skill, foreign.Id, all: true, null, null, "bob");
        var denied = await CallAsync(service, session,
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"restore_skill_version","arguments":{"name":"borrowed","version":1}}}""");
        Assert.True(denied!["result"]!["isError"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ToolErrors_AreReportedAsToolResults()
    {
        var (service, _, _) = Build();
        var response = await CallAsync(service, Session(),
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"get_skill","arguments":{"name":"missing"}}}""");
        Assert.True(response!["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("missing", response["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }
}
