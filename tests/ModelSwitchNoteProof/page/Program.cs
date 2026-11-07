// Reads a Claude Code project transcript the way the hub does (NativeTranscript.Parse) and prints
// the conversation page the web app polls (ConversationPage.FromEntries), serialized with the web
// defaults ASP.NET uses for the real endpoint. The stand-in hub serves this output as it is.
using System.Text.Json;
using AgentHub.Api.Services;

if (args.Length != 1)
{
    Console.Error.WriteLine("usage: page <transcript.jsonl>");
    return 2;
}
var entries = NativeTranscript.Parse(File.ReadAllText(args[0]));
var page = ConversationPage.FromEntries("s1", "Running", entries, 0, null);
Console.WriteLine(JsonSerializer.Serialize(page, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
return 0;
