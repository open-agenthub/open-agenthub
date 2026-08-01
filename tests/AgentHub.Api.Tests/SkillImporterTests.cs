using AgentHub.Api.Library;
using AgentHub.Api.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentHub.Api.Tests;

public class SkillImporterTests
{
    private static (SkillImporter Importer, InMemorySkillStore Skills) Build()
    {
        var skills = new InMemorySkillStore();
        return (new SkillImporter(skills, LibraryTest.SearchService(skills),
            NullLogger<SkillImporter>.Instance), skills);
    }

    private static SessionRecord Session(string? projectId = null) => new()
    {
        Id = "s1", Owner = "alice", ProjectId = projectId, CallbackToken = "t"
    };

    [Fact]
    public async Task Import_CreatesSkillInSessionScope_WithFilesAndSessionAttribution()
    {
        var (importer, skills) = Build();
        var result = await importer.ImportAsync(Session("p1"),
            [new ImportedSkill("deploy", "Deploy helper", "# v1",
                [new SkillFile("scripts/run.sh", "#!/bin/sh\necho hi")])],
            CancellationToken.None);

        Assert.Equal(["deploy"], result.Created);
        var record = (await skills.ListByOwnerAsync("alice")).Single();
        Assert.Equal("p1", record.ProjectId);
        Assert.Equal("scripts/run.sh", (await skills.GetFilesAsync(record.Id, 1)).Single().Path);
        Assert.Equal("session:s1", (await skills.ListVersionsAsync(record.Id))[0].CreatedBy);
    }

    [Fact]
    public async Task Import_SkipsUnchangedSkills_SoRepeatedSyncsAddNoVersions()
    {
        var (importer, skills) = Build();
        var payload = new ImportedSkill("deploy", "d", "# same",
            [new SkillFile("check.sh", "true")]);
        await importer.ImportAsync(Session(), [payload], CancellationToken.None);

        var second = await importer.ImportAsync(Session(), [payload], CancellationToken.None);
        Assert.Equal(["deploy"], second.Unchanged);
        Assert.Equal(1, (await skills.ListByOwnerAsync("alice")).Single().Version);

        // A content change produces a new version again.
        var third = await importer.ImportAsync(Session(),
            [payload with { Content = "# changed" }], CancellationToken.None);
        Assert.Equal(["deploy"], third.Updated);
        Assert.Equal(2, (await skills.ListByOwnerAsync("alice")).Single().Version);
    }

    [Fact]
    public async Task Import_FileChangeAlone_TriggersAnUpdate()
    {
        var (importer, skills) = Build();
        var payload = new ImportedSkill("deploy", "d", "# same", [new SkillFile("check.sh", "true")]);
        await importer.ImportAsync(Session(), [payload], CancellationToken.None);

        var result = await importer.ImportAsync(Session(),
            [payload with { Files = [new SkillFile("check.sh", "false")] }], CancellationToken.None);
        Assert.Equal(["deploy"], result.Updated);
        var record = (await skills.ListByOwnerAsync("alice")).Single();
        Assert.Equal("false", (await skills.GetFilesAsync(record.Id, 2)).Single().Content);
    }

    [Fact]
    public async Task Import_ReportsInvalidSkills_AsSkipped_AndImportsTheRest()
    {
        var (importer, skills) = Build();
        var result = await importer.ImportAsync(Session(),
            [
                new ImportedSkill("Not Kebab", null, "# x", null),
                new ImportedSkill("fine", null, "# ok", null)
            ],
            CancellationToken.None);

        Assert.Equal(["Not Kebab"], result.Skipped);
        Assert.Equal(["fine"], result.Created);
        Assert.Single(await skills.ListByOwnerAsync("alice"));
    }

    [Fact]
    public async Task Import_DoesNotTouchSameNamedSkill_InAnotherScope()
    {
        var (importer, skills) = Build();
        skills.Add("alice", "deploy", "# personal");

        await importer.ImportAsync(Session("p1"),
            [new ImportedSkill("deploy", null, "# project version", null)], CancellationToken.None);

        var all = await skills.ListByOwnerAsync("alice");
        Assert.Equal(2, all.Count);
        Assert.Equal("# personal", await skills.GetContentAsync(all.Single(r => r.ProjectId is null)));
        Assert.Equal("# project version", await skills.GetContentAsync(all.Single(r => r.ProjectId == "p1")));
    }
}
