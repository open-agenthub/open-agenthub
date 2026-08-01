using System.Text.Json;
using AgentHub.Api.Library;
using AgentHub.Api.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentHub.Api.Tests;

public class SkillSearchServiceTests
{
    /// <summary>Maps fixed phrases to fixed vectors so similarity is deterministic.</summary>
    private sealed class FakeEmbeddingProvider(Dictionary<string, float[]> vectors) : IEmbeddingProvider
    {
        public bool Enabled => true;
        public string Model => "fake-embed-1";
        public Task<float[]?> EmbedAsync(string text, CancellationToken ct = default) =>
            Task.FromResult<float[]?>(vectors.FirstOrDefault(kv => text.Contains(kv.Key)).Value);
    }

    [Fact]
    public void Cosine_HandlesIdenticalOrthogonalAndDegenerateVectors()
    {
        Assert.Equal(1.0, SkillSearchService.Cosine([1, 0], [1, 0]), 6);
        Assert.Equal(0.0, SkillSearchService.Cosine([1, 0], [0, 1]), 6);
        Assert.Equal(0.0, SkillSearchService.Cosine([1, 0], [1, 0, 0]), 6);
        Assert.Equal(0.0, SkillSearchService.Cosine([0, 0], [1, 0]), 6);
    }

    [Fact]
    public async Task Search_WithoutEmbeddings_RanksByFullTextOnly()
    {
        var skills = new InMemorySkillStore();
        skills.Add("alice", "deploy-notes", "# deploy deploy deploy");
        skills.Add("alice", "misc", "# one deploy mention");
        skills.Add("alice", "unrelated", "# nothing here");
        var service = LibraryTest.SearchService(skills);

        var accessible = await skills.ListByOwnerAsync("alice");
        var hits = await service.SearchAsync("alice", accessible, "deploy", 10);

        Assert.Equal(["deploy-notes", "misc"], hits.Select(h => h.Name));
        Assert.True(hits[0].Score > hits[1].Score);
    }

    [Fact]
    public async Task Search_BlendsVectorSimilarity_AndFindsSemanticMatches()
    {
        var skills = new InMemorySkillStore();
        var release = skills.Add("alice", "release-runbook", "# how to ship a new version");
        skills.Add("alice", "cooking", "# pasta recipes");
        var embeddings = new InMemorySkillEmbeddingStore();
        await embeddings.UpsertAsync(release.Id, "fake-embed-1", [1f, 0f]);

        var provider = new FakeEmbeddingProvider(new()
        {
            // The query shares no keyword with the skill, only the vector matches.
            ["deployment"] = [1f, 0f]
        });
        var service = new SkillSearchService(
            skills, embeddings, provider, NullLogger<SkillSearchService>.Instance);

        var accessible = await skills.ListByOwnerAsync("alice");
        var hits = await service.SearchAsync("alice", accessible, "deployment", 10);

        var hit = Assert.Single(hits);
        Assert.Equal("release-runbook", hit.Name);
        Assert.Equal(0.5, hit.Score, 3);
    }

    [Fact]
    public async Task Search_EmptyQueryOrNoAccessibleSkills_ReturnsNothing()
    {
        var skills = new InMemorySkillStore();
        skills.Add("alice", "deploy", "# d");
        var service = LibraryTest.SearchService(skills);

        Assert.Empty(await service.SearchAsync("alice", await skills.ListByOwnerAsync("alice"), "  ", 10));
        Assert.Empty(await service.SearchAsync("alice", [], "deploy", 10));
    }
}

public class SkillLibraryMcpConfigTests
{
    private static SessionRecord Session() => new()
    {
        Id = "s1", Owner = "alice", CallbackToken = "secret-token"
    };

    [Fact]
    public void BuildServer_PointsAtSessionMcpEndpoint_WithCallbackToken()
    {
        var server = SkillLibraryMcpConfig.BuildServer("http://backend.svc/", Session());
        Assert.Equal("skill-library", server.Name);

        using var doc = JsonDocument.Parse(server.ConfigJson);
        Assert.Equal("http", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("http://backend.svc/internal/sessions/s1/mcp",
            doc.RootElement.GetProperty("url").GetString());
        Assert.Equal("secret-token",
            doc.RootElement.GetProperty("headers").GetProperty("X-Agent-Token").GetString());
    }

    [Fact]
    public void Merge_InjectsSkillLibrary_ButInlineDefinitionWins()
    {
        var injected = SkillLibraryMcpConfig.BuildServer("http://backend.svc", Session());

        var merged = McpConfigAssembler.Merge(null, [injected]);
        using (var doc = JsonDocument.Parse(merged!))
        {
            Assert.True(doc.RootElement.GetProperty("mcpServers").TryGetProperty("skill-library", out _));
        }

        var inline = """{"mcpServers":{"skill-library":{"command":"custom"}}}""";
        var overridden = McpConfigAssembler.Merge(inline, [injected]);
        using (var doc = JsonDocument.Parse(overridden!))
        {
            Assert.Equal("custom", doc.RootElement.GetProperty("mcpServers")
                .GetProperty("skill-library").GetProperty("command").GetString());
        }
    }
}
