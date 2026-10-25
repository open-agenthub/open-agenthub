using AgentHub.Api.Models;
using AgentHub.Api.Services;
using k8s.Models;
using Xunit;

namespace AgentHub.Api.Tests;

public class SessionStartupFailureTests
{
    [Fact]
    public void FifthCloneFailureIdentifiesRepoBranchAndRemedyEvenWhilePodPending()
    {
        var repos = Enumerable.Range(1, 5).Select(i => new RepoRef { Url = $"https://example.com/repo{i}", Branch = "main" }).ToList();
        var pod = FailedContainer("git-clone", "repository-index: 5\ndefault-branch: instagram\nfatal: Remote branch main not found in upstream origin", true);
        var failure = SessionStartupFailure.FromPod(pod, repos)!;
        Assert.True(failure.Terminal);
        Assert.Equal("clone", failure.Step);
        Assert.Equal("clone: Branch main does not exist in https://example.com/repo5. The default branch is instagram. Edit the session's repositories.", failure.Message);
    }

    [Theory]
    [InlineData("https://user:arbitrary-secret@example.com/repo", "arbitrary-secret")]
    [InlineData("ssh://user:other-secret@example.com/repo", "other-secret")]
    [InlineData("fatal: https://example.com/repo?access_token=arbitrary-secret&x=1", "arbitrary-secret")]
    [InlineData("Authorization: Bearer arbitrary-secret\nnext line", "arbitrary-secret")]
    [InlineData("AUTHORIZATION: Basic arbitrary-secret", "arbitrary-secret")]
    [InlineData("x-api-key: arbitrary-secret", "arbitrary-secret")]
    [InlineData("Cookie: session=arbitrary-secret", "arbitrary-secret")]
    [InlineData("fatal: ghp_testsecret123 github_pat_testsecret glpat-testsecret sk-testsecret", "testsecret")]
    [InlineData("password=arbitrary-secret", "arbitrary-secret")]
    [InlineData("https://user:arbitrary-secret@example.com failed with arbitrary-secret", "arbitrary-secret")]
    [InlineData("https://user:arbitrary%2Dsecret@example.com failed with arbitrary-secret", "arbitrary-secret")]
    public void RedactsBeforeStoringOrDisplaying(string input, string secret)
    {
        var sanitized = SessionStartupFailure.Redact(input);
        Assert.DoesNotContain(secret, sanitized);
        Assert.Contains("[redacted]", sanitized);
        Assert.Equal(sanitized, SessionStartupFailure.Redact(sanitized));
    }

    [Fact]
    public void RedactsBeforeTruncationAndTrimsUnknownFailures()
    {
        var reason = SessionStartupFailure.Redact("https://user:" + new string('x', 5000) + "@example.com/repo");
        Assert.Equal("https://[redacted]@example.com/repo", reason);
        var failure = SessionStartupFailure.FromPod(FailedContainer("copy-runtime", "  unknown problem  ", true), [])!;
        Assert.Contains("unknown problem", failure.Message);
        Assert.True(failure.Terminal);
        Assert.True(SessionStartupFailure.Redact(new string('x', 5000)).Length <= 2001);
    }

    [Theory]
    [InlineData("ImagePullBackOff", "image pull")]
    [InlineData("ErrImagePull", "image pull")]
    [InlineData("CreateContainerConfigError", "agent start")]
    public void WaitingFailuresRemainRecoverable(string reason, string step)
    {
        var pod = new V1Pod { Status = new V1PodStatus { Phase = "Pending", ContainerStatuses = [new V1ContainerStatus {
            Name = "agent", State = new V1ContainerState { Waiting = new V1ContainerStateWaiting { Reason = reason, Message = "Access refused" } }
        }] } };
        var failure = SessionStartupFailure.FromPod(pod, [])!;
        Assert.False(failure.Terminal);
        Assert.Equal(step, failure.Step);
    }

    [Fact]
    public void SchedulingQuotaAgentAndHealthyStates()
    {
        var pod = new V1Pod { Status = new V1PodStatus { Phase = "Pending", Conditions = [new V1PodCondition { Type = "PodScheduled", Status = "False", Reason = "Unschedulable", Message = "Insufficient cpu" }] } };
        Assert.Equal("scheduling", SessionStartupFailure.FromPod(pod, [])!.Step);
        var quota = SessionStartupFailure.Admission("exceeded quota: cpu. token=secret-value");
        Assert.Equal("quota", quota.Step);
        Assert.DoesNotContain("secret-value", quota.Message);
        Assert.Equal("agent start", SessionStartupFailure.FromPod(FailedContainer("agent", "cannot launch", false), [])!.Step);
        Assert.Null(SessionStartupFailure.FromPod(new V1Pod { Status = new V1PodStatus { Phase = "Running" } }, []));
        Assert.Null(SessionStartupFailure.FromPod(null, []));
    }

    // #244: the agent container's own termination message, as common/server.js now writes it for
    // a CLI that exited non-zero, is what the list shows instead of Kubernetes' bare "Error".
    [Fact]
    public void AgentExitShowsTheTerminationMessageTheSessionAgentWrote()
    {
        var container = new V1ContainerStatus { Name = "agent", State = new V1ContainerState { Terminated =
            new V1ContainerStateTerminated { ExitCode = 1, Reason = "Error",
                Message = "Claude exited with code 1: Failed to authenticate: OAuth session expired and could not be refreshed" } } };
        var pod = new V1Pod { Status = new V1PodStatus { Phase = "Failed", ContainerStatuses = [container] } };

        Assert.Equal("agent start: Claude exited with code 1: Failed to authenticate: OAuth session expired and could not be refreshed",
            SessionStartupFailure.FromPod(pod, [])!.Message);
        container.State.Terminated.Message = null;
        Assert.Equal("agent start: Error", SessionStartupFailure.FromPod(pod, [])!.Message);
    }

    [Fact]
    public void AdmissionUsesThePlainKubernetesStatusMessage()
    {
        var failure = SessionStartupFailure.Admission("{\"kind\":\"Status\",\"message\":\"exceeded quota: pods, token=private-value\",\"code\":403}");
        Assert.Equal("quota", failure.Step);
        Assert.DoesNotContain("Status", failure.Message);
        Assert.DoesNotContain("private-value", failure.Message);
        Assert.Contains("Free resources", failure.Message);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("plain unknown refusal")]
    public void AdmissionPassesThroughUnknownResponseShapes(string response)
        => Assert.Contains(response, SessionStartupFailure.Admission(response).Message);

    [PostgreSqlFact]
    public async Task FailureReasonSurvivesPersistenceAndTargetedUpdates()
    {
        await using var database = await PostgresSharingDatabase.CreateAsync();
        var row = new AgentHub.Api.Persistence.SessionRecord {
            Id = "failure", Owner = "alice", Title = "Original", Mode = SessionMode.Interactive,
            AgentSessionId = "thread", CallbackToken = "callback", FailureReason = "https://user:private-value@example.com/repo"
        };
        await database.Sessions.UpsertAsync(row);
        Assert.DoesNotContain("private-value", (await database.Sessions.GetAsync("alice", "failure"))!.FailureReason!);
        await database.ExecuteAsync("UPDATE sessions SET title='Concurrent edit' WHERE id='failure'");
        await database.Sessions.SetFailureReasonAsync("failure", "Authorization: Bearer private-value", true);
        var stored = (await database.Sessions.GetAsync("alice", "failure"))!;
        Assert.Equal("Concurrent edit", stored.Title);
        Assert.Equal("Failed", stored.Status);
        Assert.DoesNotContain("private-value", stored.FailureReason!);
        stored.Status = "Pending"; stored.FailureReason = null;
        await database.Sessions.UpsertAsync(stored);
        Assert.Null((await database.Sessions.GetAsync("alice", "failure"))!.FailureReason);
    }

    public static V1Pod FailedContainer(string name, string message, bool init)
    {
        var container = new V1ContainerStatus { Name = name, State = new V1ContainerState { Terminated = new V1ContainerStateTerminated { ExitCode = 128, Message = message } } };
        return new V1Pod { Status = new V1PodStatus { Phase = "Pending", InitContainerStatuses = init ? [container] : [], ContainerStatuses = init ? [] : [container] } };
    }
}
