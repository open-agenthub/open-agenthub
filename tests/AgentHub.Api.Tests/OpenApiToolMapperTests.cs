using System.Text.Json;
using AgentHub.Api.Library.ApiMcpGateway;
using Xunit;

namespace AgentHub.Api.Tests;

public class OpenApiToolMapperTests
{
    private static string FixturePath =>
        Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "fixtures", "openapi", "petstore-mini.json"));

    [Fact]
    public void MapsPetstoreMini_ToThreeTools()
    {
        var json = File.ReadAllText(FixturePath);
        var tools = OpenApiToolMapper.MapTools(json);

        Assert.Equal(3, tools.Count);
        Assert.Contains(tools, t => t.Name == "listPets");
        Assert.Contains(tools, t => t.Name == "createPet");
        Assert.Contains(tools, t => t.Name == "getPetById");
    }

    [Fact]
    public void ListPets_HasOptionalLimitQueryAndGetMethod()
    {
        var json = File.ReadAllText(FixturePath);
        var tool = OpenApiToolMapper.MapTools(json).Single(t => t.Name == "listPets");

        Assert.Equal("GET", tool.Method);
        Assert.Equal("/pets", tool.PathTemplate);
        Assert.Contains("List all pets", tool.Description);
        Assert.True(tool.InputSchema.TryGetProperty("properties", out var props));
        Assert.True(props.TryGetProperty("limit", out _));
        Assert.False(tool.InputSchema.TryGetProperty("required", out var required)
                     && required.EnumerateArray().Any(e => e.GetString() == "limit"));
    }

    [Fact]
    public void GetPetById_RequiresPathParam()
    {
        var json = File.ReadAllText(FixturePath);
        var tool = OpenApiToolMapper.MapTools(json).Single(t => t.Name == "getPetById");

        Assert.Equal("GET", tool.Method);
        Assert.Equal("/pets/{petId}", tool.PathTemplate);
        Assert.True(tool.InputSchema.TryGetProperty("required", out var required));
        Assert.Contains(required.EnumerateArray().Select(e => e.GetString()), s => s == "petId");
    }

    [Fact]
    public void CreatePet_ExposesRequestBodyFields()
    {
        var json = File.ReadAllText(FixturePath);
        var tool = OpenApiToolMapper.MapTools(json).Single(t => t.Name == "createPet");

        Assert.Equal("POST", tool.Method);
        Assert.True(tool.InputSchema.TryGetProperty("properties", out var props));
        Assert.True(props.TryGetProperty("name", out _));
        Assert.True(tool.InputSchema.TryGetProperty("required", out var required));
        Assert.Contains(required.EnumerateArray().Select(e => e.GetString()), s => s == "name");
    }

    [Fact]
    public void ReadsDefaultBaseUrl_FromServers()
    {
        var json = File.ReadAllText(FixturePath);
        var baseUrl = OpenApiToolMapper.ReadDefaultBaseUrl(json);
        Assert.Equal("https://petstore.example.test/v1", baseUrl);
    }
}
