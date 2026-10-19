using AgentHub.Api.Services;
using Xunit;

namespace AgentHub.Api.Tests;

public class ConversationImageTests
{
    private const string FileId = "0123456789abcdef0123456789abcdef";
    private const string ImagePath = "/workspace/.agenthub/files/" + FileId + "/pasted.png";

    [Fact]
    public void ClaudeCompanionEnrichesOnlyItsPromptAcrossPolls()
    {
        var first = NativeTranscript.Parse("""
            {"type":"user","promptId":"first","message":{"content":[{"type":"image","source":{"type":"base64","data":"AA=="}}]}}
            {"type":"user","promptId":"second","message":{"content":[{"type":"text","text":"Same words"},{"type":"image"}]}}
            """);
        var companion = System.Text.Json.JsonSerializer.Serialize(new
        {
            type = "user", promptId = "first", isMeta = true, turnCompanion = true,
            message = new { content = new[] { new { type = "text", text = "[Image: source: " + ImagePath + "]" } } }
        });
        var result = NativeTranscript.Append(first, companion);
        Assert.Equal(2, result.Count);
        Assert.Equal(FileId, Assert.Single(result[0].Attachments!).Id);
        Assert.Null(result[1].Attachments);
        Assert.Null(first[0].Attachments);
        Assert.NotEqual(ConversationPage.FromEntries("s", "Running", first, null, null).ConversationId,
            ConversationPage.FromEntries("s", "Running", result, 2, null).ConversationId);
    }

    [Fact]
    public void CodexImageWrapperRetainsExactUploadedId()
    {
        var line = System.Text.Json.JsonSerializer.Serialize(new
        {
            type = "response_item", payload = new { type = "message", role = "user", content = new[] {
                new { type = "input_text", text = "<image name=[Image #1] path=\"" + ImagePath + "\">" },
                new { type = "input_text", text = "</image>\nDescribe it" } } }
        });
        Assert.Equal(FileId, Assert.Single(Assert.Single(NativeTranscript.Parse(line)).Attachments!).Id);
    }

    [Fact]
    public void OpenCodeFileSourceRetainsIdWithoutImageBytes()
    {
        var line = System.Text.Json.JsonSerializer.Serialize(new
        {
            type = "user", message = new { content = new[] { new { type = "file", source = new { path = ImagePath } } } }
        });
        var entry = Assert.Single(NativeTranscript.Parse(line));
        Assert.Equal("", entry.Text);
        Assert.Equal(FileId, Assert.Single(entry.Attachments!).Id);
    }

    [Theory]
    [InlineData("/tmp/pasted.png")]
    [InlineData("/workspace/.agenthub/files/not-an-id/pasted.png")]
    [InlineData("/workspace/.agenthub/files/0123456789abcdef0123456789abcdef/pasted.svg")]
    [InlineData("/workspace/.agenthub/files/0123456789abcdef0123456789abcdef/pasted.png.exe")]
    public void UnknownOrNonRasterPathsDoNotBecomeImages(string path)
    {
        var line = System.Text.Json.JsonSerializer.Serialize(new { type = "user", message = new { content = path } });
        Assert.Null(Assert.Single(NativeTranscript.Parse(line)).Attachments);
    }

    [Fact]
    public void AssistantAndUnmatchedCompanionsCannotAttachToNearbyUser()
    {
        var line = System.Text.Json.JsonSerializer.Serialize(new { type = "assistant", message = new { content = ImagePath } });
        Assert.Null(Assert.Single(NativeTranscript.Parse(line)).Attachments);
        var companion = System.Text.Json.JsonSerializer.Serialize(new { type = "user", isMeta = true,
            turnCompanion = true, promptId = "unknown", message = new { content = ImagePath } });
        Assert.Empty(NativeTranscript.Parse(companion));
    }
}
