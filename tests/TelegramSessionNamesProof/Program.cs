using TelegramSessionNamesProof;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.WebHost.UseUrls("http://127.0.0.1:54330");
var app = builder.Build();
var cs = Environment.GetEnvironmentVariable("AGENTHUB_TEST_POSTGRES_CONNECTION_STRING")
    ?? throw new InvalidOperationException("Set AGENTHUB_TEST_POSTGRES_CONNECTION_STRING to a disposable Postgres database.");
if (args.Contains("--deleted-sessions"))
{
    // Printed before the assertions, so a failing run still shows what went over the wire.
    var steps = await DeletedSessionScenario.RunAsync(cs, verify: false);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(steps,
        new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    DeletedSessionScenario.Verify(steps);
    Console.WriteLine("PASS deleted-session proof");
    return;
}
var verify = !args.Contains("--record-baseline");
app.MapPost("/proof/{path}", (string path) => TelegramSessionScenario.RunAsync(path, cs, verify));
await app.StartAsync();
using var client = new HttpClient();
var recordings = new Dictionary<string, List<WireMessage>>();
foreach (var path in TelegramSessionScenario.Paths)
{
    // Baseline lacks origin headers, so record it without applying the new assertions.
    using var response = await client.PostAsync("http://127.0.0.1:54330/proof/" + path, null);
    response.EnsureSuccessStatusCode();
    recordings[path] = (await response.Content.ReadFromJsonAsync<List<WireMessage>>())!;
}
Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(recordings));
await app.StopAsync();
