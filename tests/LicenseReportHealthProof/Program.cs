using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using AgentHub.Api.Admin;
using AgentHub.Api.Auth;
using AgentHub.Api.Controllers;
using AgentHub.Api.Licensing;
using AgentHub.Api.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

static void Check(bool ok, string reason) { if (!ok) throw new Exception(reason); }
static int Port() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }
var connection = Environment.GetEnvironmentVariable("AGENTHUB_TEST_POSTGRES_CONNECTION_STRING")
    ?? throw new Exception("Set AGENTHUB_TEST_POSTGRES_CONNECTION_STRING to a disposable Postgres database.");
var schema = "license_health_" + Guid.NewGuid().ToString("N");
await using var db = NpgsqlDataSource.Create(connection);
await using (var create = db.CreateCommand($"CREATE SCHEMA {schema}")) await create.ExecuteNonQueryAsync();
using var signing = ECDsa.Create(ECCurve.NamedCurves.nistP256);
var licenceId = Guid.NewGuid();
string Token(DateTime expiry) {
    string Encode(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    var header = Encode(Encoding.UTF8.GetBytes("{\"alg\":\"ES256\"}"));
    var payload = Encode(JsonSerializer.SerializeToUtf8Bytes(new { lid = licenceId, seats = 5, plan = "subscription", org = "Example team", validUntil = expiry.ToString("O") }));
    var input = header + "." + payload;
    return input + "." + Encode(signing.SignData(Encoding.ASCII.GetBytes(input), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
}
var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders();
var servicePort = Port(); var serviceUrl = $"http://127.0.0.1:{servicePort}";
builder.WebHost.UseUrls(serviceUrl);
await using var service = builder.Build();
var mode = "success"; var nextToken = Token(DateTime.UtcNow.AddDays(45));
service.MapPost("/api/usage/report", async (HttpContext ctx) => {
    Check(ctx.Request.Headers.Authorization.ToString().StartsWith("Bearer "), "Reporter omitted bearer");
    using var body = await JsonDocument.ParseAsync(ctx.Request.Body);
    Check(body.RootElement.GetProperty("instanceKey").GetString()!.Length == 64, "Missing instance binding");
    return mode switch {
        "reject" => Results.Text("private upstream diagnostic", statusCode: 503),
        "missing" => Results.Json(new { ok = true }),
        "invalid" => Results.Json(new { token = "invalid" }),
        _ => Results.Json(new { token = nextToken })
    };
});
await service.StartAsync();
var scoped = new NpgsqlConnectionStringBuilder(connection) { SearchPath = schema, Pooling = false }.ConnectionString;
var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
    ["ConnectionStrings:Postgres"] = scoped, ["Ee:License:ServiceUrl"] = serviceUrl,
    ["Ee:Admins"] = "dev", ["Ee:License:ReportWarningDays"] = "7"
}).Build();
try {
    var store = new LicenseStore(cfg); await store.InitializeAsync();
    var users = new UserDirectory(cfg); await users.InitializeAsync();
    var license = new EnterpriseLicense(store, NullLogger<EnterpriseLicense>.Instance, signing.ExportSubjectPublicKeyInfoPem());
    var initialExpiry = DateTime.UtcNow.AddDays(3); var initialToken = Token(initialExpiry); await store.SetTokenAsync(initialToken); await license.ReloadAsync();
    using var httpServices = new ServiceCollection().AddHttpClient().BuildServiceProvider();
    var reporter = new SeatUsageReporter(license, store, users, httpServices.GetRequiredService<IHttpClientFactory>(), cfg, NullLogger<SeatUsageReporter>.Instance);
    var hostBuilder = WebApplication.CreateBuilder(); hostBuilder.Logging.ClearProviders(); var apiPort = Port();
    hostBuilder.WebHost.UseUrls($"http://127.0.0.1:{apiPort}");
    hostBuilder.Configuration.AddConfiguration(cfg);
    hostBuilder.Services.AddSingleton(store); hostBuilder.Services.AddSingleton(users);
    hostBuilder.Services.AddSingleton<IEnterpriseLicense>(license);
    hostBuilder.Services.AddSingleton<AdminAccess>();
    hostBuilder.Services.AddControllers().AddApplicationPart(typeof(AdminController).Assembly);
    hostBuilder.Services.AddAuthentication("Dev").AddScheme<AuthenticationSchemeOptions, InsecureDevAuthHandler>("Dev", null);
    hostBuilder.Services.AddAuthorization();
    await using var api = hostBuilder.Build(); api.UseAuthentication(); api.UseAuthorization(); api.MapControllers();
    await api.StartAsync();
    using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{apiPort}") };
    async Task<JsonElement> Overview() => JsonDocument.Parse(await client.GetStringAsync("/api/admin/overview")).RootElement.Clone();
    var before = await Overview();
    Check(before.GetProperty("lastCheckIn").ValueKind == JsonValueKind.Null, "Initial report must be never");
    Check(before.GetProperty("reportWarning").GetBoolean(), "Three day token must warn at seven days");
    Check(Math.Abs((before.GetProperty("featuresStopAt").GetDateTime() - initialExpiry).TotalSeconds) < 1, "Stop must be signed expiry");
    mode = "reject"; await reporter.ReportOnceAsync(default);
    var failed = await Overview();
    Check(failed.GetProperty("lastReportError").GetString()!.Contains("503"), "Missing HTTP failure");
    Check(!failed.ToString().Contains("private upstream"), "Leaked service response body");
    // A fresh store and verifier model another replica/restart against the actual shared DB.
    var restartedStore = new LicenseStore(cfg); await restartedStore.InitializeAsync();
    Check((await restartedStore.GetReportErrorAsync()).Error!.Contains("503"), "Failure lost on restart");
    foreach (var failure in new[] { "missing", "invalid" }) {
        mode = failure; await reporter.ReportOnceAsync(default);
        Check(await store.GetTokenAsync() == TokenForComparison(), "Failed renewal clobbered token");
        Check((await store.GetReportErrorAsync()).Error is not null, "Missing protocol error");
    }
    string TokenForComparison() => initialToken!;
    mode = "success"; await reporter.ReportOnceAsync(default);
    var healthy = await Overview();
    Check(healthy.GetProperty("lastReportError").ValueKind == JsonValueKind.Null, "Success did not clear error");
    Check(healthy.GetProperty("lastReportErrorAt").ValueKind == JsonValueKind.Null, "Success did not clear error timestamp");
    Check(healthy.GetProperty("lastCheckIn").ValueKind == JsonValueKind.String, "Success missing timestamp");
    Check(!healthy.GetProperty("reportWarning").GetBoolean(), "Fresh 45 day token should not warn");
    var lastSuccess = await store.GetLastReportAsync();
    await service.StopAsync();
    try { await reporter.ReportOnceAsync(default); throw new Exception("Expected connectivity failure"); }
    catch (HttpRequestException) { }
    Check((await store.GetReportErrorAsync()).Error!.Contains("request failed"), "Network failure missing");
    Check(await store.GetLastReportAsync() == lastSuccess, "Failure changed last success");
    cfg["Ee:License:ReportWarningDays"] = "60";
    Check((await Overview()).GetProperty("reportWarning").GetBoolean(), "Configurable lead time ignored");
    cfg["Ee:License:ReportWarningDays"] = "0";
    Check(!(await Overview()).GetProperty("reportWarning").GetBoolean(), "Zero must warn only at expiry");
    await store.SetTokenAsync(Token(DateTime.UtcNow.AddMinutes(-1)));
    Check((await Overview()).GetProperty("reportWarning").GetBoolean(), "Expired token must warn");
    Console.WriteLine("PASS: real service HTTP rejection, invalid/missing renewal, network failure, Postgres restart persistence, success clearing, signed expiry and configurable warning via real Admin HTTP controller.");
    await api.StopAsync();
} finally {
    await service.StopAsync();
    await using var drop = db.CreateCommand($"DROP SCHEMA {schema} CASCADE"); await drop.ExecuteNonQueryAsync();
}
