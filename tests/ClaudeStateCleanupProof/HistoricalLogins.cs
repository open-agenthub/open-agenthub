using System.Formats.Tar;
using System.Text.Json;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using AgentHub.Api.Controllers;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using AgentHub.Api.Storage;
using k8s;
using k8s.Models;

public static class HistoricalLogins
{
    public static async Task RunAsync(string root, HttpClient http, S3ArtifactStore storage,
        PostgresSessionStore registry, PostgresSessionExpiryLock locks, Kubernetes cluster,
        string ns, string owner, string prefix, bool temporary = false)
    {
        var paths = new Dictionary<AgentKind, string[]> {
            [AgentKind.Claude] = [".claude/.credentials.json"],
            [AgentKind.Codex] = [".codex/auth.json"],
            [AgentKind.Cursor] = [".cursor/auth.json"],
            [AgentKind.OpenCode] = [".opencode/auth.json", ".opencode/mcp-auth.json"],
            [AgentKind.OpenClaw] = [".openclaw/auth-profiles.json", ".openclaw/agents/main/agent/auth-profiles.json", ".openclaw/agents/other/agent/openclaw-agent.sqlite", ".openclaw/agents/other/agent/openclaw-agent.sqlite-wal", ".openclaw/agents/other/agent/openclaw-agent.sqlite-shm"]
        };
        if (temporary)
        {
            var fixtures = JsonSerializer.Deserialize<TemporaryFixture[]>(await File.ReadAllTextAsync(Path.Join(root, "results.json")))!;
            paths = fixtures.ToDictionary(f => Enum.Parse<AgentKind>(f.agent, true), f => new[] { f.temporary });
            Check(paths.Count == 5, "five temporary credential fixtures");
        }
        var originals = new Dictionary<AgentKind, byte[]>();
        string Key(AgentKind agent) => IArtifactStore.StateKey(KubernetesSessionService.OwnerKey(owner), "proof-316", agent);
        async Task<byte[]> Read(AgentKind agent) { await using var input = await storage.OpenReadAsync(Key(agent)) ?? throw new Exception("Missing archive"); using var output = new MemoryStream(); await input.CopyToAsync(output); return output.ToArray(); }
        async Task Put(AgentKind agent, byte[] bytes) => Check(await storage.TryPutStreamAsync(Key(agent), new MemoryStream(bytes), "application/gzip"), "storage configured");
        foreach (var agent in paths.Keys)
        {
            var input = temporary ? Path.Join(root, agent.ToString().ToLowerInvariant() + "-legacy.tgz") : agent == AgentKind.Claude ? Path.Join(root, "old.tgz") : Path.Join(root, agent.ToString().ToLowerInvariant(), "old.tgz");
            originals[agent] = await File.ReadAllBytesAsync(input);
            await Put(agent, originals[agent]);
            await File.WriteAllBytesAsync(Path.Join(root, agent + "-before.tgz"), originals[agent]);
        }
        await storage.TryPutTextAsync(prefix + "unrelated.txt", "unrelated-kept");
        await registry.UpsertAsync(new SessionRecord { Id = "proof-316", Owner = owner, CallbackToken = "synthetic-provider-proof-only", Status = "Paused" });
        var body = new AdminClaudeStateCleanupController.CleanupRequest(owner, true);
        const string route = "/api/admin/session-storage-cleanup/provider-logins";
        http.DefaultRequestHeaders.Remove("X-Proof-Owner");
        Check((await http.PostAsJsonAsync(route, body)).StatusCode == HttpStatusCode.Unauthorized, "provider anonymous denied");
        http.DefaultRequestHeaders.Add("X-Proof-Owner", "ordinary");
        Check((await http.PostAsJsonAsync(route, body)).StatusCode == HttpStatusCode.Forbidden, "provider nonadmin denied");
        http.DefaultRequestHeaders.Remove("X-Proof-Owner"); http.DefaultRequestHeaders.Add("X-Proof-Owner", "admin");
        async Task<ClaudeStateCleanup.Result> Call(bool apply)
        {
            using var response = await http.PostAsJsonAsync(route, body with { Apply = apply }); response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<ClaudeStateCleanup.Result>() ?? throw new Exception("Missing counts");
        }
        Check((await Call(false)).Rewritten == 5, "five preview candidates");
        foreach (var agent in paths.Keys) Check(Same(originals[agent], await Read(agent)), "provider preview read-only");
        await using (await locks.TryAcquireAsync("proof-316") ?? throw new Exception("Lock unavailable"))
            Check((await Call(true)).Skipped == 5, "all providers skip held lock");
        var pod = await cluster.CoreV1.CreateNamespacedPodAsync(new V1Pod {
            Metadata = new V1ObjectMeta { Name = "session-proof-316" },
            Spec = new V1PodSpec { Containers = [new V1Container { Name = "fixture", Image = "example.com/fixture:local" }] }
        }, ns);
        Check((await Call(true)).Skipped == 5, "all providers skip actual producer");
        foreach (var agent in paths.Keys) Check(Same(originals[agent], await Read(agent)), "live provider archive preserved");
        await cluster.CoreV1.DeleteNamespacedPodAsync(pod.Metadata.Name, ns, gracePeriodSeconds: 0);
        var result = await Call(true);
        Check(result.Rewritten == 5 && result.Failed == 0, "five archives rewritten: " + result);
        var cleaned = new Dictionary<AgentKind, byte[]>();
        foreach (var agent in paths.Keys)
        {
            cleaned[agent] = await Read(agent);
            var before = Entries(originals[agent]); var after = Entries(cleaned[agent]);
            foreach (var secretPath in paths[agent]) { Check(before.ContainsKey(secretPath), "fixture includes login path"); Check(!after.ContainsKey(secretPath), "login removed: " + secretPath); }
            bool Removed(string name) => paths[agent].Contains(name) || (agent == AgentKind.Claude && (name.TrimEnd('/') == ".claude/backups" || name.StartsWith(".claude/backups/", StringComparison.Ordinal)));
            Check(before.Where(e => !Removed(e.Key)).OrderBy(e => e.Key).SequenceEqual(after.OrderBy(e => e.Key)), "byte-identical retained tar blocks: " + agent);
            await File.WriteAllBytesAsync(Path.Join(root, agent + "-after.tgz"), cleaned[agent]);
            Console.WriteLine("PASS real S3 provider scrub and retained raw-entry SHA256: " + agent);
        }
        Check((await Call(true)).Clean == 5, "five clean archives on repeat");
        foreach (var agent in paths.Keys) Check(Same(cleaned[agent], await Read(agent)), "provider object byte-idempotence");
        await Put(AgentKind.Codex, [1, 2, 3]);
        var malformed = await Call(true);
        Check(malformed.Failed == 1 && malformed.Clean == 4, "provider malformed counted independently");
        Check(Same([1, 2, 3], await Read(AgentKind.Codex)), "provider malformed preserved");
        Check(await storage.GetTextAsync(prefix + "unrelated.txt") == "unrelated-kept", "unrelated object preserved");
        Console.WriteLine("PASS provider-login endpoint authorization/preview/guards/rewrites/idempotence/malformed preservation");
    }

    private record TemporaryFixture(string agent, string temporary);

    private static bool Same(byte[] a, byte[] b) => a.AsSpan().SequenceEqual(b);
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static Dictionary<string, string> Entries(byte[] bytes)
    {
        using var compressed = new MemoryStream(bytes); using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
        using var raw = new MemoryStream(); gzip.CopyTo(raw); raw.Position = 0;
        using var reader = new TarReader(raw, leaveOpen: true); var result = new Dictionary<string, string>(); long start = 0;
        while (reader.GetNextEntry() is { } entry)
        {
            entry.DataStream?.CopyTo(Stream.Null); var end = (raw.Position + 511) / 512 * 512;
            result.Add(entry.Name, Convert.ToHexString(SHA256.HashData(raw.GetBuffer().AsSpan((int)start, (int)(end - start))))); start = end;
        }
        return result;
    }
}
