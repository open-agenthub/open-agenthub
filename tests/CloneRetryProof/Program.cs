using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using k8s.Models;

// Prints the git-clone init container that the real AgentPodSpecFactory emits for the given
// repositories ("url" or "url#branch"), as the env and command for a helper container that runs
// it unchanged in the real clone image. The script travels in an env var because a container
// argument is capped at 4 KiB; its sha256 is printed so the run can be matched to this build.
// "--map url#branch" reads a clone termination message on stdin and prints the reason the
// session list would show for it, so a message from a real run can be checked end to end.
if (args.FirstOrDefault() == "--map")
{
    var mapped = args.Skip(1).Select(arg => arg.Split('#', 2))
        .Select(parts => new RepoRef { Url = parts[0], Branch = parts.Length > 1 ? parts[1] : null }).ToList();
    var status = new V1ContainerStatus { Name = "git-clone", State = new V1ContainerState { Terminated =
        new V1ContainerStateTerminated { ExitCode = 128, Reason = "Error", Message = Console.In.ReadToEnd() } } };
    var failure = SessionStartupFailure.FromPod(new V1Pod { Status = new V1PodStatus { Phase = "Pending", InitContainerStatuses = [status] } }, mapped)!;
    Console.WriteLine(failure.Message);
    return;
}
var repos = args.Select(arg => arg.Split('#', 2))
    .Select(parts => new RepoRef { Url = parts[0], Branch = parts.Length > 1 ? parts[1] : null }).ToList();
var request = new CreateSessionRequest { Agent = AgentKind.Claude, AuthMode = AgentAuthMode.Subscription, Mode = SessionMode.Interactive, Repos = repos };
var record = new SessionRecord { Id = "proof", Owner = "owner", Title = "Proof", Mode = SessionMode.Interactive, Agent = AgentKind.Claude, AuthMode = AgentAuthMode.Subscription, AgentSessionId = "proof", CallbackToken = "proof" };
var context = new PodBuildContext
{
    Owner = "owner", CredentialsSecretName = "creds", ClaudeCredentialSecretName = "claude", CodexCredentialSecretName = "codex",
    CursorCredentialSecretName = "cursor", OpenClawCredentialSecretName = "openclaw", OpenCodeCredentialSecretName = "opencode",
    CallbackUrl = "http://callback.example.com", StatePutUrl = "", StateGetUrl = "", ScrollbackPutUrl = "", TranscriptPutUrl = "",
    RuntimeImages = new AgentRuntimeImages("claude", "codex", "cursor", "openclaw", "opencode", "Always"),
    Runtime = new AgentPodRuntimeSettings { AgentPort = 7681, GitCloneImage = new AgentPodRuntimeSettings().GitCloneImage, MaxCpu = "2", MaxMemory = "4Gi" }
};
var clone = AgentPodSpecFactory.Build(record, request, context).InitContainers.Single(c => c.Name == "git-clone");
var script = clone.Command[2];
var env = clone.Env.ToDictionary(e => e.Name, e => e.Value);
env["HOME"] = "/tmp/home";
env["CLONE_SCRIPT"] = script;
Console.WriteLine(JsonSerializer.Serialize(new
{
    image = clone.Image,
    sha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(script))),
    env,
    command = new[] { "/bin/sh", "-c",
        "mkdir -p \"$HOME\"; printf '%s' \"$CLONE_SCRIPT\" > /tmp/clone.sh; sha256sum /tmp/clone.sh; " +
        "start=$(date +%s); /bin/sh /tmp/clone.sh; code=$?; echo \"EXIT=$code after $(($(date +%s) - start))s\"; " +
        "ls /workspace/repo 2>/dev/null; sleep 86400" }
}, new JsonSerializerOptions { WriteIndented = true }));
