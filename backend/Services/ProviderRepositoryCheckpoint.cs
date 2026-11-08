using System.Net.Http.Json;
using System.Text.Json;
using AgentHub.Api.Persistence;
using k8s.Models;

namespace AgentHub.Api.Services;

public sealed partial class KubernetesSessionService
{
    public async Task<RepositoryCheckpoint> InspectProviderRepositoriesAsync(string owner, string id, CancellationToken ct)
    {
        var record = await _store.GetAsync(owner, id, ct) ?? throw new KeyNotFoundException();
        if (record.WorkspacePersistent || !_opts.RepositoryProviderSwitchEnabled || !SessionWorkspaceVolumes.Supported(record)
            || record.ProviderSwitchPending)
            throw new ProviderReplacementRefusedException("Repository checks are unavailable for this session.");
        return await InspectProviderRepositoriesAsync(owner, record, await RequireSourcePodAsync(record, null, ct), ct);
    }

    private async Task<RepositoryCheckpoint> InspectProviderRepositoriesAsync(string owner, SessionRecord record, V1Pod pod, CancellationToken ct)
    {
        var repos = ParseRepos(record);
        if (repos.Count is < 1 or > 32) throw new ProviderReplacementRefusedException("Configure between one and 32 repositories before switching.");
        // Resolve the same selected Git store used at spawn; the source's editable global config
        // is not evidence that a fresh target will be able to fetch the recorded commit.
        var git = await BuildGitCredentialsAsync(owner, record, repos, ct);
        using var message = new HttpRequestMessage(HttpMethod.Post,
            new UriBuilder("http", pod.Status.PodIP, _opts.AgentPort, "agenthub/repository-checkpoint").Uri);
        message.Headers.Add("X-Agent-Token", record.CallbackToken);
        message.Content = JsonContent.Create(new
        {
            repositories = AgentPodSpecFactory.DestFor(repos).Select(item => new { destination = item.dest, url = item.repo.Url }),
            credentials = git?.Store ?? ""
        });
        using var response = await _podRequests!.SendAsync(record.Id, message, HttpCompletionOption.ResponseHeadersRead, TimeSpan.FromSeconds(120), ct);
        if (!response.IsSuccessStatusCode) throw new ProviderReplacementRefusedException("Repository checks failed. Check Git access, supported checkout layout and runtime version, then retry.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var limited = new MemoryStream();
        var buffer = new byte[4096];
        int count;
        while ((count = await stream.ReadAsync(buffer, ct)) != 0)
        {
            if (limited.Length + count > 65536) throw new ProviderReplacementRefusedException("Repository checks exceed the checkpoint limit.");
            limited.Write(buffer, 0, count);
        }
        var manifest = JsonSerializer.Deserialize<RepositoryCheckpoint>(limited.ToArray(), new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new ProviderReplacementRefusedException("Repository checkpoint is missing.");
        manifest.Validate(requireReady: false);
        return manifest;
    }

    private async Task<RepositoryCheckpoint?> RequireExecutionRepositoryCheckpointAsync(SessionRecord record, CancellationToken ct)
    {
        if (record.WorkspacePersistent || record.ExecutionGeneration == 0) return null;
        var manifest = _providerSwitches is null ? null
            : await _providerSwitches.RepositoryCheckpointAsync(record.Owner, record.Id, record.ExecutionGeneration, ct);
        if (manifest is null) throw new ProviderReplacementRefusedException("The execution's repository checkpoint is missing; code restoration is held.");
        manifest.Validate();
        return manifest;
    }

    private async Task RequireUnchangedProviderRepositoriesAsync(SessionRecord record, V1Pod source, RepositoryCheckpoint expected, CancellationToken ct)
    {
        var actual = await InspectProviderRepositoriesAsync(record.Owner, record, source, ct);
        actual.Validate();
        if (expected.ReviewHash != actual.ReviewHash)
            throw new ProviderReplacementRefusedException("Repository state or omitted files changed after review. This replacement is held; cancel before source stop and review again.");
    }
}
