// Stand-in for the session callback routes an unattended runtime hook calls (#244). /agent-policy
// runs the backend's real AgentPolicyMatcher and /permission answers the way
// InternalController.RequestPermission does for an auto-approving session, so a hook is judged by
// what production decides rather than by a canned answer that ignores the command. The fake in
// the #160 proof answered "allow" for any input, which is how a hard deny on redirects shipped.
// Usage: AutonomousPolicyProof <port> <log.jsonl> [autoApprove=true]
using System.Net;
using System.Text;
using System.Text.Json;
using AgentHub.Api.Models;
using AgentHub.Api.Services;

var port = int.Parse(args[0]);
var log = args[1];
var autoApprove = args.Length < 3 || bool.Parse(args[2]);
var listener = new HttpListener();
listener.Prefixes.Add($"http://127.0.0.1:{port}/");
listener.Start();
Console.WriteLine($"listening {port} autoApprove={autoApprove}");
while (true)
{
    var ctx = await listener.GetContextAsync();
    var path = ctx.Request.Url!.AbsolutePath;
    var body = await new StreamReader(ctx.Request.InputStream).ReadToEndAsync();
    object answer;
    if (path.EndsWith("/agent-policy"))
    {
        using var doc = JsonDocument.Parse(body);
        var tool = doc.RootElement.GetProperty("tool").GetString() ?? "";
        var input = doc.RootElement.TryGetProperty("input", out var i) ? i.Clone() : default;
        var result = AgentPolicyMatcher.Decide(new AgentPolicy(), tool, input, autoApprove);
        answer = new { decision = result.Decision, reason = result.Reason };
    }
    else if (path.EndsWith("/permission"))
        answer = autoApprove ? new { decision = "allow" } : new { decision = "deny" };
    else
    {
        ctx.Response.StatusCode = 404;
        ctx.Response.Close();
        continue;
    }
    var json = JsonSerializer.Serialize(answer);
    await File.AppendAllTextAsync(log, JsonSerializer.Serialize(new { path, body, answer }) + "\n");
    var bytes = Encoding.UTF8.GetBytes(json);
    ctx.Response.ContentType = "application/json";
    await ctx.Response.OutputStream.WriteAsync(bytes);
    ctx.Response.Close();
}
