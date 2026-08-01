using AgentHub.Api.Library;
using AgentHub.Api.Storage;
using Xunit;

namespace AgentHub.Api.Tests;

public class SkillVersioningPostgresTests
{
    [PostgreSqlFact]
    public async Task Update_CreatesNewVersion_AndHistoryStaysReadable()
    {
        await using var db = await PostgresLibraryDatabase.CreateAsync();
        var created = await db.Skills.CreateAsync(
            "alice", new SaveSkillRequest("deploy", "Deploy helper", "# v1"));
        Assert.Equal(1, created.Version);

        var updated = await db.Skills.UpdateAsync("alice", created.Id,
            new SaveSkillRequest("deploy", "Deploy helper", "# v2", Comment: "tightened steps"));
        Assert.Equal(2, updated.Version);
        Assert.Equal("# v2", await db.Skills.GetContentAsync(updated));

        var versions = await db.Skills.ListVersionsAsync(created.Id);
        Assert.Equal([2, 1], versions.Select(v => v.Version));
        Assert.Equal("tightened steps", versions[0].Comment);
        Assert.Equal("alice", versions[0].CreatedBy);
        Assert.Equal("# v1", await db.Skills.GetVersionContentAsync(created.Id, 1));
    }

    [PostgreSqlFact]
    public async Task Restore_RepublishesOldContent_AsNewHeadVersion()
    {
        await using var db = await PostgresLibraryDatabase.CreateAsync();
        var created = await db.Skills.CreateAsync("alice", new SaveSkillRequest("deploy", null, "# v1"));
        await db.Skills.UpdateAsync("alice", created.Id, new SaveSkillRequest("deploy", null, "# v2"));

        var restored = await db.Skills.RestoreVersionAsync("alice", created.Id, 1, "session:s1");
        Assert.Equal(3, restored.Version);
        Assert.Equal("# v1", await db.Skills.GetContentAsync(restored));

        var versions = await db.Skills.ListVersionsAsync(created.Id);
        Assert.Equal("session:s1", versions[0].CreatedBy);
        Assert.Equal("Restored version 1.", versions[0].Comment);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            db.Skills.RestoreVersionAsync("alice", created.Id, 99));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            db.Skills.RestoreVersionAsync("bob", created.Id, 1));
    }

    [PostgreSqlFact]
    public async Task ProjectScope_AllowsSameName_PerProjectButNotWithin()
    {
        await using var db = await PostgresLibraryDatabase.CreateAsync();
        await db.Skills.CreateAsync("alice", new SaveSkillRequest("notes", null, "# personal"));
        var inProject = await db.Skills.CreateAsync(
            "alice", new SaveSkillRequest("notes", null, "# project", ProjectId: "p1"));
        Assert.Equal("p1", inProject.ProjectId);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            db.Skills.CreateAsync("alice", new SaveSkillRequest("notes", null, "# dup", ProjectId: "p1")));

        var all = await db.Skills.ListByOwnerAsync("alice");
        Assert.Equal(2, all.Count);
    }

    [PostgreSqlFact]
    public async Task Search_FindsByContent_AndRespectsTheAccessibleSet()
    {
        await using var db = await PostgresLibraryDatabase.CreateAsync();
        var runbook = await db.Skills.CreateAsync("alice",
            new SaveSkillRequest("release-runbook", "Ship it", "# kubernetes deployment steps"));
        var other = await db.Skills.CreateAsync("alice",
            new SaveSkillRequest("cooking", "Pasta", "# noodles"));

        var hits = await db.Skills.SearchAsync([runbook.Id, other.Id], "kubernetes", 10);
        Assert.Equal("release-runbook", Assert.Single(hits).Record.Name);
        Assert.True(hits[0].Rank > 0);

        // Substring fallback: partial words match names via ILIKE.
        var partial = await db.Skills.SearchAsync([runbook.Id, other.Id], "runbook", 10);
        Assert.Single(partial);

        // Out-of-set ids never leak into results.
        Assert.Empty(await db.Skills.SearchAsync([other.Id], "kubernetes", 10));
    }

    [PostgreSqlFact]
    public async Task Files_RoundTripPerVersion_AndSurviveRestore()
    {
        await using var db = await PostgresLibraryDatabase.CreateAsync();
        var created = await db.Skills.CreateAsync("alice", new SaveSkillRequest(
            "deploy", null, "# v1",
            Files: [new SkillFile("scripts/check.sh", "true"), new SkillFile("notes.md", "# n")]));
        Assert.Equal(2, (await db.Skills.GetFilesAsync(created.Id, 1)).Count);

        // Update without files carries the current set into the new version.
        await db.Skills.UpdateAsync("alice", created.Id, new SaveSkillRequest("deploy", null, "# v2"));
        Assert.Equal(2, (await db.Skills.GetFilesAsync(created.Id, 2)).Count);

        // Replacing with an empty list clears the files for the new version only.
        await db.Skills.UpdateAsync("alice", created.Id,
            new SaveSkillRequest("deploy", null, "# v3", Files: []));
        Assert.Empty(await db.Skills.GetFilesAsync(created.Id, 3));
        Assert.Equal(2, (await db.Skills.GetFilesAsync(created.Id, 1)).Count);

        // Restoring v1 brings its files back.
        var restored = await db.Skills.RestoreVersionAsync("alice", created.Id, 1);
        Assert.Equal(4, restored.Version);
        Assert.Equal(["notes.md", "scripts/check.sh"],
            (await db.Skills.GetFilesAsync(created.Id, 4)).Select(f => f.Path).OrderBy(p => p));
    }

    [PostgreSqlFact]
    public async Task Files_LiveInS3WhenConfigured_AndDeleteCleansThem()
    {
        await using var db = await PostgresLibraryDatabase.CreateAsync(s3Enabled: true);
        var created = await db.Skills.CreateAsync("alice", new SaveSkillRequest(
            "deploy", null, "# v1", Files: [new SkillFile("scripts/check.sh", "true")]));

        Assert.Equal("true",
            db.Artifacts.Objects[IArtifactStore.SkillFileKey(created.Id, 1, "scripts/check.sh")]);
        Assert.Equal("true", (await db.Skills.GetFilesAsync(created.Id, 1)).Single().Content);

        await db.Skills.DeleteAsync("alice", created.Id);
        Assert.DoesNotContain(db.Artifacts.Objects.Keys, k => k.Contains(created.Id));
    }

    [PostgreSqlFact]
    public async Task S3Mode_ArchivesEveryVersion_AndDeleteCleansAllOfThem()
    {
        await using var db = await PostgresLibraryDatabase.CreateAsync(s3Enabled: true);
        var created = await db.Skills.CreateAsync("alice", new SaveSkillRequest("deploy", null, "# v1"));
        await db.Skills.UpdateAsync("alice", created.Id, new SaveSkillRequest("deploy", null, "# v2"));

        Assert.Equal("# v1", db.Artifacts.Objects[IArtifactStore.SkillVersionKey(created.Id, 1)]);
        Assert.Equal("# v2", db.Artifacts.Objects[IArtifactStore.SkillVersionKey(created.Id, 2)]);
        Assert.Equal("# v2", db.Artifacts.Objects[IArtifactStore.SkillKey(created.Id)]);
        Assert.Equal("# v1", await db.Skills.GetVersionContentAsync(created.Id, 1));

        await db.Skills.DeleteAsync("alice", created.Id);
        Assert.DoesNotContain(db.Artifacts.Objects.Keys, k => k.Contains(created.Id));
        Assert.Empty(await db.Skills.ListVersionsAsync(created.Id));
    }
}
