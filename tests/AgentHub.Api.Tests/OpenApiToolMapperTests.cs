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

    [Fact]
    public void MergesPathItemParameters_WithOperationOverride()
    {
        const string json = """
            {
              "openapi": "3.0.0",
              "info": { "title": "t", "version": "1" },
              "paths": {
                "/items/{itemId}": {
                  "parameters": [
                    {
                      "name": "itemId",
                      "in": "path",
                      "required": true,
                      "schema": { "type": "string" }
                    },
                    {
                      "name": "trace",
                      "in": "header",
                      "required": false,
                      "schema": { "type": "string" }
                    },
                    {
                      "name": "limit",
                      "in": "query",
                      "required": false,
                      "schema": { "type": "integer", "maximum": 10 }
                    }
                  ],
                  "get": {
                    "operationId": "getItem",
                    "parameters": [
                      {
                        "name": "limit",
                        "in": "query",
                        "required": true,
                        "schema": { "type": "integer", "maximum": 100 }
                      }
                    ],
                    "responses": { "200": { "description": "ok" } }
                  }
                }
              }
            }
            """;

        var tool = OpenApiToolMapper.MapTools(json).Single(t => t.Name == "getItem");

        Assert.Contains(tool.Parameters, p => p is { Name: "itemId", In: "path", Required: true });
        Assert.Contains(tool.Parameters, p => p is { Name: "trace", In: "header", Required: false });
        var limit = Assert.Single(tool.Parameters, p => p.Name == "limit" && p.In == "query");
        Assert.True(limit.Required); // operation wins over path-item

        Assert.True(tool.InputSchema.TryGetProperty("properties", out var props));
        Assert.True(props.TryGetProperty("itemId", out _));
        Assert.True(props.TryGetProperty("trace", out _));
        Assert.Equal(100, props.GetProperty("limit").GetProperty("maximum").GetInt32());
        Assert.True(tool.InputSchema.TryGetProperty("required", out var required));
        var requiredNames = required.EnumerateArray().Select(e => e.GetString()).ToHashSet();
        Assert.Contains("itemId", requiredNames);
        Assert.Contains("limit", requiredNames);
    }
}
