using AgentHub.Api.Services;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Browser;
using k8s;
using k8s.Models;
using System.Net;

const string image = "node:22-bookworm-slim";
var session = new SessionRecord { Id = "tmp-proof", Owner = "proof", Title = "Proof", Agent = AgentKind.Claude, AuthMode = AgentAuthMode.Auto, Mode = SessionMode.Interactive, CallbackToken = "synthetic" };
var spec = AgentPodSpecFactory.Build(session, new CreateSessionRequest { Title = "Proof", Agent = AgentKind.Claude, Cpu = "1", Memory = "1Gi" }, new PodBuildContext
{
    Owner = "proof", CredentialsSecretName = "unused", ClaudeCredentialSecretName = "unused", CodexCredentialSecretName = "unused", CursorCredentialSecretName = "unused", OpenClawCredentialSecretName = "unused", OpenCodeCredentialSecretName = "unused",
    CallbackUrl = "http://example.com/internal", StatePutUrl = "", StateGetUrl = "", ScrollbackPutUrl = "", TranscriptPutUrl = "",
    RuntimeImages = new AgentRuntimeImages(image, image, image, image, image, "IfNotPresent"),
    Runtime = new AgentPodRuntimeSettings { AgentPort = 7681, MaxCpu = "2", MaxMemory = "4Gi" }
});
// Only startup and secret plumbing are replaced: the actual factory's tmp mount, init,
// pod security and container security are what the kubelet must admit and exercise.
var container = spec.Containers.Single();
container.Command = ["/bin/sh", "-c", "sleep 3600"];
container.Env = [];
container.ReadinessProbe = null;
container.VolumeMounts = container.VolumeMounts.Where(m => m.Name is "tmp" or "home").ToList();
spec.Volumes = spec.Volumes.Where(v => v.Name is "tmp" or "home").ToList();
spec.ServiceAccountName = "default";
Emit("agent-tmp-proof", spec);
var browser = BrowserPodSpecFactory.Build(session, BrowserLease.Pending("tmp-proof", "proof", new byte[32]), new BrowserPodContext
{
    Namespace = "tmp-proof", ControlNamespace = "tmp-proof", CallbackUrl = "http://example.com", LeaseToken = "synthetic", AgentPodIp = IPAddress.Parse("127.0.0.2"),
    Options = new BrowserOptions { Image = image, PullPolicy = "IfNotPresent" }
}).Pod.Spec;
browser.Containers.Single().Command = ["/bin/sh", "-c", "sleep 3600"];
browser.Containers.Single().ReadinessProbe = null;
browser.ServiceAccountName = "default";
Emit("browser-tmp-proof", browser);
void Emit(string name, V1PodSpec podSpec)
{
    var pod = new V1Pod { ApiVersion = "v1", Kind = "Pod", Metadata = new V1ObjectMeta { Name = name, NamespaceProperty = "tmp-proof" }, Spec = podSpec };
    File.WriteAllText(Path.Join(args[0], name + ".json"), KubernetesJson.Serialize(pod));
}
