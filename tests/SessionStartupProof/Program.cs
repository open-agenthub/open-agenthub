using AgentHub.Api.Services;
using AgentHub.Api.Models;
using k8s;
using k8s.Models;
var config = KubernetesClientConfiguration.BuildConfigFromConfigFile(args.Single());
using var client = new Kubernetes(config);
var ns = "startup-proof-" + Guid.NewGuid().ToString("N")[..8];
try { await client.CoreV1.CreateNamespaceAsync(new V1Namespace { Metadata = new V1ObjectMeta { Name = ns } }); } catch (k8s.Autorest.HttpOperationException ex) when ((int)ex.Response.StatusCode == 409) { }
var repos = Enumerable.Range(1, 5).Select(i => new RepoRef { Url = $"https://example.com/repo{i}", Branch = "main" }).ToList();
async Task Check(string name, V1PodStatus status, string expectedStep, bool terminal)
{
    await client.CoreV1.CreateNamespacedPodAsync(new V1Pod {
        Metadata = new V1ObjectMeta { Name = name },
        Spec = new V1PodSpec { RestartPolicy = "Never", Containers = [new V1Container { Name = "agent", Image = "example.com/agent:proof" }],
            InitContainers = [new V1Container { Name = "git-clone", Image = "example.com/git:proof" }] }
    }, ns);
    var pod = await client.CoreV1.ReadNamespacedPodAsync(name, ns);
    pod.Status = status;
    await client.CoreV1.ReplaceNamespacedPodStatusAsync(pod, name, ns);
    var read = await client.CoreV1.ReadNamespacedPodAsync(name, ns);
    var failure = SessionStartupFailure.FromPod(read, repos) ?? throw new Exception("No diagnostic");
    if (failure.Step != expectedStep || failure.Terminal != terminal || failure.Message.Contains("private-value")) throw new Exception("Incorrect mapping");
    Console.WriteLine($"REAL API {name}: phase={read.Status.Phase}, step={failure.Step}, terminal={failure.Terminal}, reason={failure.Message}");
}
V1ContainerStatus Clone(V1ContainerState state) => new() { Name = "git-clone", Image = "example.com/git:proof", ImageID = "", Ready = false, RestartCount = 0, State = state };
await Check("clone-failure", new V1PodStatus { Phase = "Pending", InitContainerStatuses = [Clone(new V1ContainerState { Terminated = new V1ContainerStateTerminated { ExitCode = 128, Reason = "Error", Message = "repository-index: 5\ndefault-branch: instagram\nfatal: Remote branch main not found in upstream origin\nAuthorization: Bearer private-value" } })] }, "clone", true);
await Check("image-failure", new V1PodStatus { Phase = "Pending", InitContainerStatuses = [Clone(new V1ContainerState { Waiting = new V1ContainerStateWaiting { Reason = "ImagePullBackOff", Message = "Failed pulling https://user:private-value@example.com/image" } })] }, "image pull", false);
await Check("scheduling-failure", new V1PodStatus { Phase = "Pending", Conditions = [new V1PodCondition { Type = "PodScheduled", Status = "False", Reason = "Unschedulable", Message = "0/1 nodes available: Insufficient cpu" }] }, "scheduling", false);
await Check("agent-failure", new V1PodStatus { Phase = "Failed", ContainerStatuses = [new V1ContainerStatus { Name = "agent", Image = "example.com/agent:proof", ImageID = "", Ready = false, RestartCount = 0, State = new V1ContainerState { Terminated = new V1ContainerStateTerminated { ExitCode = 1, Message = "agent could not start: token=private-value" } } }] }, "agent start", true);
Console.WriteLine("PASS: four statuses written/read through a real kube-apiserver; no kubelet used.");

await client.CoreV1.DeleteNamespaceAsync(ns);
