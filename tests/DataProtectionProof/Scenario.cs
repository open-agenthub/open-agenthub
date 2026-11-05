using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using AgentHub.Api.DataProtection;
using AgentHub.Api.Library;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace DataProtectionProof;

public static class Scenario
{
    public static (string Certificate, string Key) Certificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=proof.example.com", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return (certificate.ExportCertificatePem(), rsa.ExportPkcs8PrivateKeyPem());
    }

    public static async Task RunAsync(string connectionString, string backendDll)
    {
        var schema = "dp_proof_" + Guid.NewGuid().ToString("N");
        await Sql(connectionString, $"CREATE SCHEMA {schema}");
        var cs = new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = schema, Pooling = false }.ConnectionString;
        var cert = Certificate();
        var apiKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var root = Directory.CreateTempSubdirectory("data-protection-proof-");
        try
        {
            await using var a = new Replica(backendDll, cs, root.FullName, "a", apiKey, cert);
            await using var b = new Replica(backendDll, cs, root.FullName, "b", apiKey, cert);
            a.Start(); b.Start();
            await Task.WhenAll(a.Healthy(), b.Healthy());
            var saved = await a.Post("api/mcp-servers", new { name = "shared", kind = "raw", configJson = "{\"command\":\"node\"}", secretJson = "{\"token\":\"synthetic-proof-value\"}" });
            var id = saved.GetProperty("id").GetString()!;
            await CheckCatalog(a, false); await CheckCatalog(b, false);
            var webhook = await a.Post("api/webhook-triggers", new { name = "shared", promptTemplate = "review" });
            var webhookId = webhook.GetProperty("trigger").GetProperty("id").GetString()!;
            var secret = webhook.GetProperty("secret").GetString()!;
            await CheckWebhook(b, webhookId, secret);
            Console.WriteLine("PASS: real replicas A/B decrypt catalog and webhook values through HTTP");

            await a.Stop(); a.Start(); await a.Healthy(); await CheckCatalog(a, false);
            await a.Stop();
            await using var replacement = new Replica(backendDll, cs, root.FullName, "replacement", apiKey, cert);
            replacement.Start(); await replacement.Healthy(); await CheckCatalog(replacement, false);
            await CheckWebhook(replacement, webhookId, secret);
            Require(!Directory.Exists(Path.Join(replacement.Home, ".aspnet", "DataProtection-Keys")), "a local key ring was created");
            Console.WriteLine("PASS: process restart and replacement with empty HOME retain readable values; no HOME key ring");

            var xml = (string)(await Scalar(cs, "SELECT xml FROM data_protection_keys LIMIT 1"))!;
            Require(xml.Contains("encryptedSecret") && !xml.Contains("<masterKey"), "key is not certificate-wrapped");
            Console.WriteLine("PASS: Postgres ring contains encryptedSecret, no plaintext masterKey");

            // The same registration used by Program also covers transient token consumers.
            using (var providerA = Provider(cs, cert))
            using (var providerB = Provider(cs, cert))
            {
                var tokensA = new McpGatewayTokenService(providerA.GetRequiredService<IDataProtectionProvider>());
                var tokensB = new McpGatewayTokenService(providerB.GetRequiredService<IDataProtectionProvider>());
                var token = tokensA.Issue("session", id, "admin");
                Require(tokensB.TryValidate(token, id, out _), "cross-provider gateway token rejected");
                Require(!tokensB.TryValidate(token, "other", out _), "gateway binding lost");
                var cleanupA = providerA.GetRequiredService<IDataProtectionProvider>().CreateProtector("session-storage-cleanup-v1", "admin");
                var cleanupB = providerB.GetRequiredService<IDataProtectionProvider>().CreateProtector("session-storage-cleanup-v1", "admin");
                Require(cleanupB.Unprotect(cleanupA.Protect("plan")) == "plan", "cleanup plan rejected");
            }
            Console.WriteLine("PASS: gateway token and owner-bound cleanup protector use the shared registration");

            await Sql(cs, "UPDATE mcp_servers SET secret_json='lost-key-payload'; UPDATE webhook_triggers SET secret='lost-key-payload'");
            await CheckCatalog(b, true);
            var triggers = await b.Get("api/webhook-triggers");
            Require(triggers[0].GetProperty("needsReentry").GetBoolean(), "webhook recovery state missing");
            Require((string)(await Scalar(cs, "SELECT secret_json FROM mcp_servers"))! == "lost-key-payload", "unreadable value changed");
            await b.Put("api/mcp-servers/" + id, new { name = "shared", kind = "raw", configJson = "{\"command\":\"node\"}", secretJson = (string?)null });
            await CheckCatalog(b, true);
            await b.Put("api/mcp-servers/" + id, new { name = "shared", kind = "raw", configJson = "{\"command\":\"node\"}", secretJson = "replacement" });
            await CheckCatalog(replacement, false);
            Console.WriteLine("PASS: unreadable catalog/webhook GETs return 200 with needsReentry; null update preserves ciphertext; explicit re-entry repairs");

            await using var wrong = new Replica(backendDll, cs, root.FullName, "wrong", apiKey, Certificate());
            wrong.Start();
            await wrong.ExitsUnhealthy();
            Console.WriteLine("PASS: wrong wrapping certificate stops startup rather than replacing the ring");

            // Simulate a surviving pre-upgrade key directory, with its original app discriminator.
            var legacyPath = Directory.CreateDirectory(Path.Join(root.FullName, "legacy-ring"));
            var legacy = DataProtectionProvider.Create(legacyPath, options => options.SetApplicationName("legacy-proof"));
            var oldPayload = legacy.CreateProtector(McpSecretProtector.Purpose).Protect("legacy-readable");
            await using (var connection = new NpgsqlConnection(cs))
            {
                await connection.OpenAsync();
                await using var update = new NpgsqlCommand("UPDATE mcp_servers SET secret_json=@payload", connection);
                update.Parameters.AddWithValue("payload", oldPayload);
                await update.ExecuteNonQueryAsync();
            }
            await using var migrate = new Replica(backendDll, cs, root.FullName, "migrate", apiKey, cert, legacyPath.FullName);
            migrate.Start(); await migrate.Healthy(); await CheckCatalog(migrate, false);
            Require((string)(await Scalar(cs, "SELECT secret_json FROM mcp_servers"))! != oldPayload, "legacy payload was not migrated");
            await CheckCatalog(b, false);
            Console.WriteLine("PASS: surviving legacy ring migrates readable stored values into Postgres without losing them");
        }
        finally
        {
            await Sql(connectionString, $"DROP SCHEMA {schema} CASCADE");
            root.Delete(true);
        }
    }

    private static ServiceProvider Provider(string cs, (string Certificate, string Key) cert)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["ConnectionStrings:Postgres"] = cs, ["DataProtection:CertificatePem"] = cert.Certificate,
            ["DataProtection:PrivateKeyPem"] = cert.Key }).Build();
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<IConfiguration>(config);
        services.AddSharedDataProtection(config);
        return services.BuildServiceProvider();
    }

    private static async Task CheckCatalog(Replica replica, bool needsReentry)
    {
        var list = await replica.Get("api/mcp-servers");
        Require(list.GetArrayLength() == 1, "catalog entry missing");
        Require(list[0].GetProperty("hasSecret").GetBoolean(), "secret presence lost");
        Require(list[0].GetProperty("needsReentry").GetBoolean() == needsReentry, "wrong recovery state");
        Require(!list[0].TryGetProperty("secretJson", out _), "secret returned by HTTP");
    }

    private static async Task CheckWebhook(Replica replica, string id, string secret)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/git/webhooks/" + id) { Content = JsonContent.Create(new { }) };
        request.Headers.Add("X-Gitlab-Token", secret);
        using var response = await replica.Http.SendAsync(request);
        Require(response.IsSuccessStatusCode, "cross-replica webhook rejected: " + response.StatusCode);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task Sql(string cs, string sql)
    {
        await using var connection = new NpgsqlConnection(cs); await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection); await command.ExecuteNonQueryAsync();
    }
    private static async Task<object?> Scalar(string cs, string sql)
    {
        await using var connection = new NpgsqlConnection(cs); await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection); return await command.ExecuteScalarAsync();
    }

    private sealed class Replica : IAsyncDisposable
    {
        private Process? _process;
        private readonly ProcessStartInfo _info;
        private readonly System.Text.StringBuilder _log = new();
        public string Home { get; }
        public HttpClient Http { get; }
        public Replica(string dll, string cs, string root, string name, string apiKey,
            (string Certificate, string Key) cert, string? legacyPath = null)
        {
            Home = Path.Join(root, name);
            Directory.CreateDirectory(Path.Join(Home, ".kube"));
            File.WriteAllText(Path.Join(Home, ".kube", "config"), """
                apiVersion: v1
                kind: Config
                clusters: [{name: none, cluster: {server: "http://127.0.0.1:1"}}]
                users: [{name: none, user: {token: unused}}]
                contexts: [{name: none, context: {cluster: none, user: none, namespace: agenthub-sessions}}]
                current-context: none
                """);
            using var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start();
            var port = ((IPEndPoint)socket.LocalEndpoint).Port;
            Http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromSeconds(5) };
            Http.DefaultRequestHeaders.Authorization = new("Bearer", apiKey);
            _info = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet") {
                WorkingDirectory = Path.GetDirectoryName(dll)!, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            _info.ArgumentList.Add(dll);
            foreach (var (key, value) in new Dictionary<string, string> {
                ["HOME"] = Home, ["ASPNETCORE_ENVIRONMENT"] = "Production", ["Kestrel__Endpoints__Http__Url"] = Http.BaseAddress.ToString(),
                ["ConnectionStrings__Postgres"] = cs, ["Auth__ApiKey"] = apiKey, ["AgentHub__Namespace"] = "agenthub-sessions", ["Browser__Enabled"] = "false",
                ["DataProtection__CertificatePem"] = cert.Certificate, ["DataProtection__PrivateKeyPem"] = cert.Key }) _info.Environment[key] = value;
            if (legacyPath is not null) {
                _info.Environment["DataProtection__LegacyKeysDirectory"] = legacyPath;
                _info.Environment["DataProtection__LegacyApplicationName"] = "legacy-proof";
            }
        }
        public void Start()
        {
            _process = new Process { StartInfo = _info };
            _process.OutputDataReceived += (_, e) => { lock (_log) _log.AppendLine(e.Data); };
            _process.ErrorDataReceived += (_, e) => { lock (_log) _log.AppendLine(e.Data); };
            _process.Start(); _process.BeginOutputReadLine(); _process.BeginErrorReadLine();
        }
        public async Task Healthy()
        {
            for (var i = 0; i < 120; i++) {
                if (_process!.HasExited) throw new InvalidOperationException("Replica exited: " + _log);
                try { if ((await Http.GetAsync("healthz")).IsSuccessStatusCode) return; }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { }
                await Task.Delay(250);
            }
            throw new TimeoutException("Replica not healthy: " + _log);
        }
        public async Task ExitsUnhealthy()
        {
            await _process!.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Require(_process.ExitCode != 0, "wrong certificate unexpectedly started");
        }
        public async Task<JsonElement> Get(string path) => await Read(await Http.GetAsync(path));
        public async Task<JsonElement> Post(string path, object body) => await Read(await Http.PostAsJsonAsync(path, body));
        public async Task Put(string path, object body) => _ = await Read(await Http.PutAsJsonAsync(path, body));
        private async Task<JsonElement> Read(HttpResponseMessage response)
        {
            using (response) {
                if (!response.IsSuccessStatusCode) throw new InvalidOperationException("HTTP " + response.StatusCode + ": " + await response.Content.ReadAsStringAsync() + "\n" + _log);
                return await response.Content.ReadFromJsonAsync<JsonElement>();
            }
        }
        public async Task Stop()
        {
            if (_process is null) return;
            if (!_process.HasExited) _process.Kill(true);
            await _process.WaitForExitAsync(); _process.Dispose(); _process = null;
        }
        public async ValueTask DisposeAsync() { await Stop(); Http.Dispose(); }
    }
}
