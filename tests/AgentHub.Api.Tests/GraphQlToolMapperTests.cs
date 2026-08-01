using System.Text.Json;
using AgentHub.Api.Library.ApiMcpGateway;
using Xunit;

namespace AgentHub.Api.Tests;

public class GraphQlToolMapperTests
{
    private static string FixturePath =>
        Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "fixtures", "graphql", "books.graphql"));

    [Fact]
    public void MapsBooksSchema_ToThreeFieldTools()
    {
        var sdl = File.ReadAllText(FixturePath);
        var tools = GraphQlToolMapper.MapToolsFromSdl(sdl);

        Assert.Equal(3, tools.Count);
        Assert.Contains(tools, t => t.Name == "book");
        Assert.Contains(tools, t => t.Name == "books");
        Assert.Contains(tools, t => t.Name == "addBook");
    }

    [Fact]
    public void BookTool_IsQueryWithRequiredId()
    {
        var sdl = File.ReadAllText(FixturePath);
        var tool = GraphQlToolMapper.MapToolsFromSdl(sdl).Single(t => t.Name == "book");

        Assert.Equal(GraphQlOperationKind.Query, tool.Kind);
        Assert.Equal("book", tool.FieldName);
        Assert.Contains("Fetch a book by id", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.True(tool.InputSchema.TryGetProperty("required", out var required));
        Assert.Contains(required.EnumerateArray().Select(e => e.GetString()), s => s == "id");
        Assert.True(tool.InputSchema.TryGetProperty("properties", out var props));
        Assert.True(props.TryGetProperty("id", out _));
    }

    [Fact]
    public void AddBookTool_IsMutationWithTitleAndAuthor()
    {
        var sdl = File.ReadAllText(FixturePath);
        var tool = GraphQlToolMapper.MapToolsFromSdl(sdl).Single(t => t.Name == "addBook");

        Assert.Equal(GraphQlOperationKind.Mutation, tool.Kind);
        Assert.Equal("addBook", tool.FieldName);
        Assert.True(tool.InputSchema.TryGetProperty("properties", out var props));
        Assert.True(props.TryGetProperty("title", out _));
        Assert.True(props.TryGetProperty("author", out _));
        Assert.True(tool.InputSchema.TryGetProperty("required", out var required));
        var names = required.EnumerateArray().Select(e => e.GetString()).ToHashSet();
        Assert.Contains("title", names);
        Assert.Contains("author", names);
    }

    [Fact]
    public void MapsIntrospectionJson_ToSameFieldTools()
    {
        // Compact introspection shape covering Query.book / Query.books / Mutation.addBook
        const string introspection = """
            {
              "data": {
                "__schema": {
                  "queryType": { "name": "Query" },
                  "mutationType": { "name": "Mutation" },
                  "types": [
                    {
                      "kind": "OBJECT",
                      "name": "Query",
                      "fields": [
                        {
                          "name": "book",
                          "description": "Fetch a book by id",
                          "args": [
                            {
                              "name": "id",
                              "type": {
                                "kind": "NON_NULL",
                                "ofType": { "kind": "SCALAR", "name": "ID" }
                              }
                            }
                          ],
                          "type": { "kind": "OBJECT", "name": "Book" }
                        },
                        {
                          "name": "books",
                          "description": null,
                          "args": [],
                          "type": {
                            "kind": "NON_NULL",
                            "ofType": {
                              "kind": "LIST",
                              "ofType": {
                                "kind": "NON_NULL",
                                "ofType": { "kind": "OBJECT", "name": "Book" }
                              }
                            }
                          }
                        }
                      ]
                    },
                    {
                      "kind": "OBJECT",
                      "name": "Mutation",
                      "fields": [
                        {
                          "name": "addBook",
                          "description": "Add a book",
                          "args": [
                            {
                              "name": "title",
                              "type": {
                                "kind": "NON_NULL",
                                "ofType": { "kind": "SCALAR", "name": "String" }
                              }
                            },
                            {
                              "name": "author",
                              "type": {
                                "kind": "NON_NULL",
                                "ofType": { "kind": "SCALAR", "name": "String" }
                              }
                            }
                          ],
                          "type": {
                            "kind": "NON_NULL",
                            "ofType": { "kind": "OBJECT", "name": "Book" }
                          }
                        }
                      ]
                    }
                  ]
                }
              }
            }
            """;

        var tools = GraphQlToolMapper.MapToolsFromIntrospection(introspection);
        Assert.Equal(3, tools.Count);
        Assert.Contains(tools, t => t is { Name: "book", Kind: GraphQlOperationKind.Query });
        Assert.Contains(tools, t => t is { Name: "books", Kind: GraphQlOperationKind.Query });
        Assert.Contains(tools, t => t is { Name: "addBook", Kind: GraphQlOperationKind.Mutation });
    }
}
