using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentHub.Api.Controllers;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using k8s;
using k8s.Models;

namespace AgentHub.Api.Services;

public sealed class ProviderReplacementRefusedException(string message) : InvalidOperationException(message);

public sealed record ProviderSwitchSelection(Guid RequestId, ProviderSwitchTarget Target,
    HandoverPreviewRequest Handover, string? ReviewHash = null);
public sealed record ProviderSwitchReview(string Text, string ReviewHash, ProviderSwitchTarget Target,
    HandoverCapacity Capacity, string Policy);
public sealed record ProviderSwitchAvailability(bool Available, string Reason, ProviderSwitchOperation? Operation, IReadOnlyList<ExecutionUsageSnapshot>? CompletedUsage = null);

public sealed partial class KubernetesSessionService
{
    private static void RefusePendingReplacement(SessionRecord record)
    {
        if (record.ProviderSwitchPending) throw new ProviderReplacementRefusedException("Provider replacement is pending; retry or cancel that operation before other maintenance.");
    }

    private const string ReplacementAnnotation = "agenthub.dev/provider-switch";
    private const string ExecutionAnnotation = "agenthub.dev/execution";
    private static string? Unavailable(SessionRecord record, bool enabled) =>
        !record.WorkspacePersistent ? "Switching not available for this session: its workspace uses emptyDir. Migration is not supported; create a new persistent-workspace session."
        : !enabled ? "Switching not available for this session: the administrator has disabled persistent-workspace switch admission."
        : !SessionWorkspaceVolumes.Supported(record) ? "Switching requires a packaged, non-root Claude or Codex interactive terminal."
        : record.ProviderSwitchPending ? "A provider replacement is already pending."
        : null;

    public async Task<ProviderSwitchAvailability> ProviderSwitchStatusAsync(string owner, string id, CancellationToken ct)
    {
        var record = await _store.GetAsync(owner, id, ct) ?? throw new KeyNotFoundException();
        var reason = Unavailable(record, _opts.WorkspaceVolumePersistent)
            ?? (_workspaceVolumes is null || _providerSwitches is null || _executions is null || !_artifacts.IsConfigured || _runtimeUpdateLock is null || _podRequests is null || _switchMessages is null || _switchDelivery is null
                ? "Switching requires configured state storage and lifecycle services." : null);
        return new(reason is null, reason ?? "Available at a completed-turn safe point after review.",
            _providerSwitches is null ? null : await _providerSwitches.GetAsync(owner, id, ct),
            _executionUsage is null ? null : await _executionUsage.ReadAsync(owner, id, ct));
    }

    public async Task<ProviderSwitchReview> ReviewProviderSwitchAsync(string owner, string id, ProviderSwitchSelection selection, CancellationToken ct)
    {
        var record = await _store.GetAsync(owner, id, ct) ?? throw new KeyNotFoundException();
        if (Unavailable(record, _opts.WorkspaceVolumePersistent) is { } reason) throw new ProviderReplacementRefusedException(reason);
        if (_workspaceVolumes is null || _providerSwitches is null || _executions is null || _runtimeUpdateLock is null || _podRequests is null || _switchMessages is null || _switchDelivery is null || !_artifacts.IsConfigured)
            throw new ProviderReplacementRefusedException("Provider switching requires configured state storage and lifecycle services.");
        if (selection.RequestId == Guid.Empty || selection.Target.Agent == record.Agent || selection.Handover.Target != selection.Target.Agent)
            throw new ArgumentException("Choose the other supported provider and a replacement request ID.");
        var target = await PreflightProviderTargetAsync(record, selection.Target, ct);
        var pod = await RequireSourcePodAsync(record, null, ct);
        var workspace = await _workspaceVolumes!.EnsureAsync(record, ct) ?? throw new ProviderReplacementRefusedException("Recorded workspace is missing.");
        var framed = HandoverPreviewBuilder.Build(selection.Handover).Text.Replace("Provider handover preview to", "Provider handover to", StringComparison.Ordinal)
            + $"\n\nReplacement request: {selection.RequestId:D}";
        if (framed.Length > HandoverPreviewBuilder.MaxCharacters) throw new ArgumentException("Handover including request marker exceeds 16000 characters.");
        var capacity = await TargetCapacityAsync(owner, target, framed, ct);
        var digest = ReviewDigest(record, pod.Metadata.Uid, workspace.ClaimUid!, target, selection.RequestId, framed);
        return new(framed, digest, target, capacity,
            "The session's approval mode, tool restrictions, MCP access, secret selection and network policy are preserved. Previous approvals are not transferred. Only the reviewed text enters fresh native context; visible history is not model context.");
    }

    public async Task<ProviderSwitchOperation> StartProviderSwitchAsync(string owner, string id, ProviderSwitchSelection selection, CancellationToken ct)
    {
        await using var held = await SwitchLockAsync(id, ct);
        _ = await _store.GetAsync(owner, id, ct) ?? throw new KeyNotFoundException();
        var journal = _providerSwitches ?? throw new ProviderReplacementRefusedException("Provider replacement services are unavailable.");
        var existing = await journal.GetAsync(owner, id, ct);
        if (existing is not null && existing.Request.RequestId == selection.RequestId)
        {
            if (selection.ReviewHash is null || selection.ReviewHash != existing.Request.ReviewHash)
                throw new ProviderReplacementRefusedException("Replacement request ID already has a different review.");
            return existing;
        }
        var review = await ReviewProviderSwitchAsync(owner, id, selection, ct);
        if (selection.ReviewHash != review.ReviewHash) throw new ProviderReplacementRefusedException("The reviewed selection or source changed. Review the handover again.");
        var record = (await _store.GetAsync(owner, id, ct))!;
        var pod = await RequireSourcePodAsync(record, null, ct);
        var workspace = (await _workspaceVolumes!.EnsureAsync(record, ct))!;
        await _executions!.EnsureLegacyAsync(owner, id, ct);
        await journal.CreateAsync(owner, new(selection.RequestId, id, record.ExecutionGeneration, record.Agent,
            pod.Metadata.Uid, workspace.ClaimUid!, review.Target, review.Text, review.ReviewHash), ct);
        return (await journal.GetAsync(owner, id, ct))!;
    }

    private async Task<ProviderSwitchTarget> PreflightProviderTargetAsync(SessionRecord source, ProviderSwitchTarget target, CancellationToken ct)
    {
        if (target.Agent is not (AgentKind.Claude or AgentKind.Codex) || target.Agent == source.Agent)
            throw new ArgumentException("Choose the other supported provider.");
        AgentConfiguration.ValidateForCreate(target.Agent, target.AuthMode);
        var (url, model) = AgentConfiguration.ValidateModelEndpoint(target.Agent, target.AuthMode, target.ModelEndpointUrl, target.ModelName);
        await EnsureAgentAllowedAsync(target.Agent, ct);
        await EnforceUsageLimitAsync(source.Owner, target.Agent, target.AuthMode, source.Mode, ct);
        string? credential = null;
        if (target.AuthMode == AgentAuthMode.Subscription)
        {
            var secret = await ReadSecretOrNullAsync(ProviderSecretName(source.Owner, target.Agent), ct);
            var accounts = ProviderAccountSecret.Read(secret?.Data, target.Agent);
            credential = ProviderSwitchCredentials.SelectAccount(accounts, target.CredentialId, DateTime.UtcNow);
            if (credential is null) throw new ProviderReplacementRefusedException("A usable target provider account is required before stopping the source.");
        }
        else if (target.CredentialId is not null) throw new ArgumentException("A provider account applies only to subscription authentication.");
        if (target.AuthMode == AgentAuthMode.ApiKey && !await HasSecretKeyAsync(CredsSecretName(source.Owner),
                target.Agent == AgentKind.Claude ? "anthropic_api_key" : "openai_api_key", ct))
            throw new ProviderReplacementRefusedException("Store the target provider API key before switching.");
        return target with { CredentialId = credential, ModelEndpointUrl = url, ModelName = model };
    }

    private async Task<HandoverCapacity> TargetCapacityAsync(string owner, ProviderSwitchTarget target, string text, CancellationToken ct)
    {
        var stored = _switchCatalogs is null ? null : await _switchCatalogs.GetAsync(ModelCatalogKey.For(owner, target.Agent,
            target.AuthMode, target.CredentialId, target.ModelEndpointUrl), ct);
        var catalog = stored?.Catalog;
        var usable = catalog?.State is "ok" or "fallback" && catalog.Source == (target.AuthMode == AgentAuthMode.CustomEndpoint ? "endpoint" : "cli");
        var model = usable ? target.ModelName is null ? catalog!.Models.FirstOrDefault(item => item.IsDefault == true)
            : catalog!.Models.FirstOrDefault(item => item.Id == target.ModelName) : null;
        if (target.ModelName is not null && model is null)
            throw new ProviderReplacementRefusedException("The target model must be in its account or endpoint model catalog. Refresh discovery or use the native default.");
        if (target.AuthMode == AgentAuthMode.CustomEndpoint && (stored is null || DateTimeOffset.UtcNow - stored.FetchedAt > ModelCatalogSanitizer.FreshFor))
            throw new ProviderReplacementRefusedException("Refresh the custom endpoint's model capacity before switching.");
        return ProviderHandoverCapacity.Check(target.Agent, target.AuthMode, text, model?.Capabilities?.ContextWindow);
    }

    private async Task<V1Pod> RequireSourcePodAsync(SessionRecord record, string? expectedUid, CancellationToken ct)
    {
        var pod = await TryReadPodAsync("session-" + record.Id, ct);
        if (pod is null || pod.Metadata.DeletionTimestamp is not null || pod.Status?.Phase != "Running"
            || pod.Status.ContainerStatuses?.Any(item => item.Name == "agent" && item.Ready) != true
            || (expectedUid is not null && pod.Metadata.Uid != expectedUid))
            throw new ProviderReplacementRefusedException("The source execution is not the recorded ready pod.");
        return pod;
    }

    private async Task<IAsyncDisposable> SwitchLockAsync(string id, CancellationToken ct) =>
        _runtimeUpdateLock is null ? throw new ProviderReplacementRefusedException("Session lifecycle lock is unavailable.")
            : await _runtimeUpdateLock.TryAcquireAsync(id, ct) ?? throw new ProviderReplacementRefusedException("Session maintenance is already in progress.");

    private static string ReviewDigest(SessionRecord record, string podUid, string claimUid, ProviderSwitchTarget target, Guid request, string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            { record.Id, record.ExecutionGeneration, record.AgentPolicyJson, record.AllowedToolsJson, record.AutoApprove,
                record.McpConfigJson, record.McpServerIdsJson, record.SecretIdsJson, record.SecretGrantIdsJson, record.SecretCeilingJson,
                record.GitPatIdsJson, record.SystemPrompt, Pod = podUid, Claim = claimUid, Target = target, Request = request, Text = text }))));
}

public sealed partial class KubernetesSessionService
{
    public async Task RetryProviderSwitchAsync(string owner, string id, Guid requestId, CancellationToken ct)
    {
        await using var held = await SwitchLockAsync(id, ct);
        var operation = await _providerSwitches!.GetAsync(owner, id, ct) ?? throw new KeyNotFoundException();
        if (operation.Request.RequestId != requestId || operation.Progress.Phase is "completed" or "cancelled")
            throw new ProviderReplacementRefusedException("This replacement cannot be retried.");
        await _providerSwitches.MoveAsync(owner, id, requestId, operation.Progress.Phase, operation.Progress with { Error = null }, ct);
    }

    public async Task CancelProviderSwitchAsync(string owner, string id, Guid requestId, CancellationToken ct)
    {
        await using var held = await SwitchLockAsync(id, ct);
        var operation = await _providerSwitches!.GetAsync(owner, id, ct) ?? throw new KeyNotFoundException();
        if (operation.Request.RequestId != requestId || operation.Progress.Phase is not ("requested" or "prepared" or "cancelling" or "cancelled"))
            throw new ProviderReplacementRefusedException("Source stop has begun; cancellation would be ambiguous. Retry this replacement or delete the session.");
        if (operation.Progress.Phase == "cancelled") return;
        var record = await _store.GetAsync(owner, id, ct) ?? throw new KeyNotFoundException();
        await RequireSourcePodAsync(record, operation.Request.SourcePodUid, ct);
        var progress = operation.Progress;
        if (progress.Phase != "cancelling")
        {
            progress = progress with { Phase = "cancelling", Error = null };
            await _providerSwitches.MoveAsync(owner, id, requestId, operation.Progress.Phase, progress, ct);
        }
        await FinishProviderCancellationAsync(record, operation.Request, progress, ct);
    }

    private async Task FinishProviderCancellationAsync(SessionRecord record, ProviderSwitchRequest request, ProviderSwitchProgress progress, CancellationToken ct)
    {
        if (record.ExecutionGeneration != request.SourceGeneration)
            throw new ProviderReplacementRefusedException("The cancellation source generation changed.");
        var pod = await RequireSourcePodAsync(record, request.SourcePodUid, ct);
        // Open the durable source-generation inbox gate before waking it, and retain the
        // maintenance fence until the idempotent runtime cancellation is acknowledged.
        await RuntimeSwitchAsync(record, pod, HttpMethod.Delete, new { requestId = request.RequestId }, ct);
        if (!await DeliverReleasedProviderInboxAsync(record, pod, ct)) return;
        await _providerSwitches!.MoveAsync(record.Owner, record.Id, request.RequestId, "cancelling", progress with { Phase = "cancelled", Error = null }, ct);
    }

    public async Task ProcessProviderSwitchAsync(string owner, string id, CancellationToken ct)
    {
        if (_providerSwitches is null || _runtimeUpdateLock is null) return;
        await using var held = await _runtimeUpdateLock.TryAcquireAsync(id, ct);
        if (held is null) return;
        var operation = await _providerSwitches.GetAsync(owner, id, ct);
        if (operation is null || operation.Progress.Error is not null || operation.Progress.Phase is "completed" or "cancelled") return;
        var request = operation.Request;
        var progress = operation.Progress;
        try
        {
            var record = await _store.GetAsync(owner, id, ct) ?? throw new KeyNotFoundException();
            if (progress.Phase == "cancelling")
            {
                await FinishProviderCancellationAsync(record, request, progress, ct);
                return;
            }
            var workspace = await _workspaceVolumes!.EnsureAsync(record, ct) ?? throw new ProviderReplacementRefusedException("Workspace is unavailable.");
            if (workspace.ClaimUid != request.ClaimUid) throw new ProviderReplacementRefusedException("Workspace identity changed.");
            if (progress.Phase == "requested")
            {
                if (record.ExecutionGeneration != request.SourceGeneration || record.Agent != request.SourceAgent)
                    throw new ProviderReplacementRefusedException("The source generation changed.");
                await PreflightProviderTargetAsync(record, request.Target, ct);
                await TargetCapacityAsync(owner, request.Target, request.Handover, ct);
                if (_switchPermissions is null || (await _switchPermissions.GetPendingRequestsAsync(id, ct)).Count > 0)
                    throw new ProviderReplacementRefusedException("Resolve outstanding approvals before switching.");
                await RefuseReplacementHelpersAsync(record, ct);
                var source = await RequireSourcePodAsync(record, request.SourcePodUid, ct);
                var reserved = await RuntimeSwitchAsync(record, source, HttpMethod.Post, new { requestId = request.RequestId, action = "prepare" }, ct);
                if (!reserved.Checkpointed || string.IsNullOrWhiteSpace(reserved.NativeId)) throw new ProviderReplacementRefusedException("Source checkpoint was not acknowledged.");
                var sourceText = await ReadTargetTranscriptAsync(record, source, ct, tail: true);
                if (string.IsNullOrEmpty(sourceText) || NativeTranscript.Parse(sourceText).Count == 0)
                    throw new ProviderReplacementRefusedException("Source transcript checkpoint is unavailable.");
                var entries = RetainedSource(NativeTranscript.Parse(sourceText), request.SourceGeneration, request.SourceAgent);
                progress = progress with { Phase = "prepared", SourceNativeId = reserved.NativeId, SourceEntries = entries };
                await _providerSwitches.MoveAsync(owner, id, request.RequestId, "requested", progress, ct);
            }
            if (progress.Phase == "prepared")
            {
                // Check credentials again before the irreversible source stop. This reads only
                // target references and never applies target billing/account state to the source.
                await PreflightProviderTargetAsync(record, request.Target, ct);
                await TargetCapacityAsync(owner, request.Target, request.Handover, ct);
                await RefuseReplacementHelpersAsync(record, ct);
                progress = progress with { Phase = "stopping" };
                await _providerSwitches.MoveAsync(owner, id, request.RequestId, "prepared", progress, ct);
            }
            if (progress.Phase == "stopping")
            {
                if (!progress.SourceStopAcknowledged)
                {
                    var nativeSource = await RequireSourcePodAsync(record, request.SourcePodUid, ct);
                    var stopped = await RuntimeSwitchAsync(record, nativeSource, HttpMethod.Post,
                        new { requestId = request.RequestId, action = "stop" }, ct);
                    if (!stopped.Stopped || !stopped.Checkpointed || stopped.NativeId != progress.SourceNativeId)
                        throw new ProviderReplacementRefusedException("The native source stop and final checkpoint were not acknowledged.");
                    var finalText = await ReadTargetTranscriptAsync(record, nativeSource, ct, tail: true);
                    if (string.IsNullOrEmpty(finalText)) throw new ProviderReplacementRefusedException("The final source transcript is unavailable.");
                    progress = progress with { SourceStopAcknowledged = true, SourceEntries = RetainedSource(NativeTranscript.Parse(finalText), request.SourceGeneration, request.SourceAgent) };
                    await _providerSwitches.MoveAsync(owner, id, request.RequestId, "stopping", progress, ct);
                }
                await _browsers.StopAsync(id, ct);
                if (_containerCleanup is not null) await _containerCleanup.CleanupSessionAsync(id, ct);
                var source = await TryReadPodAsync("session-" + id, ct);
                if (source is not null)
                {
                    if (source.Metadata.Uid != request.SourcePodUid) throw new ProviderReplacementRefusedException("Source pod identity changed; replacement is held.");
                    await _k8s.CoreV1.DeleteNamespacedPodAsync(source.Metadata.Name, _opts.Namespace,
                        body: new V1DeleteOptions { Preconditions = new V1Preconditions { Uid = request.SourcePodUid },
                            GracePeriodSeconds = _opts.PauseGracePeriodSeconds }, cancellationToken: ct);
                }
                await WaitForWorkspaceConsumersAsync(workspace.ClaimName, ct);
                progress = progress with { Phase = "source-stopped" };
                await _providerSwitches.MoveAsync(owner, id, request.RequestId, "stopping", progress, ct);
            }
            if (progress.Phase == "source-stopped")
            {
                await WaitForWorkspaceConsumersAsync(workspace.ClaimName, ct);
                await PreflightProviderTargetAsync(record, request.Target, ct);
                await TargetCapacityAsync(owner, request.Target, request.Handover, ct);
                await _providerSwitches.AdmitTargetAsync(owner, id, request.RequestId, ct);
                progress = progress with { Phase = "target-admitted" };
            }
            record = await _store.GetAsync(owner, id, ct) ?? throw new KeyNotFoundException();
            if (progress.Phase == "target-admitted")
            {
                // Persist intent before create. A retry reconciles only this annotated pod;
                // absence after an ambiguous attempt never authorizes replaying its prompt.
                progress = progress with { Phase = "target-starting" };
                await _providerSwitches.MoveAsync(owner, id, request.RequestId, "target-admitted", progress, ct);
                var sessionRequest = ReplacementSessionRequest(record);
                await SpawnAsync(owner, record, sessionRequest, resume: false, ct, request);
            }
            if (progress.Phase == "target-starting")
            {
                var target = await RequireReplacementPodAsync(record, request, null, ct);
                progress = progress with { Phase = "target-started", TargetPodUid = target.Metadata.Uid };
                await _providerSwitches.MoveAsync(owner, id, request.RequestId, "target-starting", progress, ct);
            }
            if (progress.Phase == "target-started")
            {
                var target = await RequireReplacementPodAsync(record, request, progress.TargetPodUid, ct);
                if (target.Status?.Phase is "Failed" or "Succeeded") throw new ProviderReplacementRefusedException("Target runtime exited before handover confirmation.");
                if (target.Status?.Phase != "Running" || target.Status.ContainerStatuses?.Any(item => item.Name == "agent" && item.Ready) != true) return;
                var state = await RuntimeSwitchAsync(record, target, HttpMethod.Get, null, ct);
                if (!state.Ready || !state.MarkerSeen || !Guid.TryParse(state.NativeId, out _)) return;
                var native = await ReadTargetTranscriptAsync(record, target, ct);
                if (NativeTranscript.Parse(native).FirstOrDefault(entry => entry.Role == "user")?.Text != request.Handover)
                    throw new ProviderReplacementRefusedException("The target's first native user turn does not match the reviewed handover.");
                var checkpoint = await RuntimeSwitchAsync(record, target, HttpMethod.Post,
                    new { requestId = request.RequestId, action = "checkpoint", nativeId = state.NativeId }, ct);
                if (!checkpoint.Checkpointed) throw new ProviderReplacementRefusedException("Target checkpoint was not acknowledged.");
                await _executions!.SaveTranscriptAsync(owner, id, record.ExecutionGeneration, native, ct);
                progress = progress with { Phase = "committing", TargetNativeId = state.NativeId };
                await _providerSwitches.MoveAsync(owner, id, request.RequestId, "target-started", progress, ct);
            }
            if (progress.Phase == "committing")
            {
                await RequireReplacementPodAsync(record, request, progress.TargetPodUid, ct);
                var history = await _executions!.ReadAsync(owner, id, ct) ?? throw new ProviderReplacementRefusedException("Execution history is missing.");
                if (history.Current.Generation == request.SourceGeneration)
                    await _executions.AdvanceAsync(owner, id, request.SourceGeneration, request.Target.Agent,
                        progress.TargetNativeId!, request.Target.CredentialId, progress.SourceEntries ?? throw new ProviderReplacementRefusedException("Source checkpoint is missing."), ct);
                else if (history.Current.Generation != request.SourceGeneration + 1 || history.Current.NativeId != progress.TargetNativeId)
                    throw new ProviderReplacementRefusedException("The committed execution does not match this replacement.");
                progress = progress with { Phase = "releasing" };
                await _providerSwitches.MoveAsync(owner, id, request.RequestId, "committing", progress, ct);
            }
            if (progress.Phase == "releasing")
            {
                var target = await RequireReplacementPodAsync(record, request, progress.TargetPodUid, ct);
                var released = await RuntimeSwitchAsync(record, target, HttpMethod.Post,
                    new { requestId = request.RequestId, action = "release", nativeId = progress.TargetNativeId }, ct);
                if (!released.Checkpointed || released.Draining) throw new ProviderReplacementRefusedException("Target release was not acknowledged.");
                if (!await DeliverReleasedProviderInboxAsync(record, target, ct)) return;
                await _providerSwitches.MoveAsync(owner, id, request.RequestId, "releasing", progress with { Phase = "completed", Error = null }, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            _log.LogWarning(error, "Provider replacement stopped; explicit owner retry is required.");
            // Do not expose provider responses, secret-bearing URLs or exception messages.
            var actual = await _providerSwitches.GetAsync(owner, id, ct);
            if (actual is null || actual.Request.RequestId != request.RequestId || actual.Progress.Phase is "completed" or "cancelled") return;
            progress = actual.Progress;
            var safeReason = error is ProviderReplacementRefusedException ? error.Message
                : "Replacement is held. Inspect session startup status and retry the same request; uncertain target input is never replayed.";
            await _providerSwitches.MoveAsync(owner, id, request.RequestId, progress.Phase, progress with { Error = safeReason }, ct);
        }
    }

    private async Task<bool> DeliverReleasedProviderInboxAsync(SessionRecord record, V1Pod pod, CancellationToken ct)
    {
        // Codex takes its inbox through native turn hooks. Claude uses its mod instead,
        // so messages held during replacement need the ordinary acknowledged push path.
        if (record.Agent != AgentKind.Claude) return true;
        if (_switchMessages is null || _switchDelivery is null)
            throw new ProviderReplacementRefusedException("Message delivery services are unavailable; replacement release is held.");
        var pending = await _switchMessages.ReadUndeliveredAsync(record.Id, AgentMessaging.InboxBatchLimit, ct);
        foreach (var message in pending)
        {
            var delivered = await _switchDelivery.TryInjectReleasedAsync(ToInfo(record, "Running", pod.Status.PodIP), message, message.FromTitle, ct);
            if (delivered.Via == MessageDeliveryVia.Inbox)
                throw new ProviderReplacementRefusedException("Queued message delivery is not confirmed. Retry the same operation after the runtime is ready.");
        }
        return pending.Count < AgentMessaging.InboxBatchLimit;
    }

    private async Task RefuseReplacementHelpersAsync(SessionRecord record, CancellationToken ct)
    {
        if (_containerCleanup is AgentHub.Api.Containers.SessionContainerService { Enabled: true } helpers
            && (await helpers.ListAsync(record, ct)).Count > 0)
            throw new ProviderReplacementRefusedException("Stop this session's helper containers before switching providers.");
    }

    private async Task WaitForWorkspaceConsumersAsync(string claim, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        while (true)
        {
            var pods = await _k8s.CoreV1.ListNamespacedPodAsync(_opts.Namespace, cancellationToken: deadline.Token);
            if (!pods.Items.Any(pod => pod.Spec?.Volumes?.Any(volume => volume.PersistentVolumeClaim?.ClaimName == claim) == true)) return;
            await Task.Delay(500, deadline.Token);
        }
    }

    private async Task<V1Pod> RequireReplacementPodAsync(SessionRecord record, ProviderSwitchRequest request, string? uid, CancellationToken ct)
    {
        var pod = await TryReadPodAsync("session-" + record.Id, ct);
        bool Annotation(string name, string expected) => pod?.Metadata.Annotations is { } annotations
            && annotations.TryGetValue(name, out var value) && value == expected;
        if (pod is null || pod.Metadata.DeletionTimestamp is not null || (uid is not null && pod.Metadata.Uid != uid)
            || !Annotation(ReplacementAnnotation, request.RequestId.ToString("D"))
            || !Annotation(ExecutionAnnotation, (request.SourceGeneration + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)))
            throw new ProviderReplacementRefusedException("The recorded target pod is unavailable. An uncertain start is never replayed.");
        return pod;
    }

    private sealed record RuntimeSwitchError(string Error);
    private sealed record RuntimeSwitchState(bool Ready, string? NativeId, string? RequestId, bool Draining, bool MarkerSeen, bool Checkpointed, bool Stopped = false);
    private async Task<RuntimeSwitchState> RuntimeSwitchAsync(SessionRecord record, V1Pod pod, HttpMethod method, object? body, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(pod.Status?.PodIP)) throw new ProviderReplacementRefusedException("Runtime address is unavailable.");
        using var request = new HttpRequestMessage(method, new UriBuilder("http", pod.Status.PodIP, _opts.AgentPort, "agenthub/provider-switch").Uri);
        request.Headers.Add("X-Agent-Token", record.CallbackToken);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await _podRequests!.SendAsync(record.Id, request, HttpCompletionOption.ResponseContentRead, TimeSpan.FromSeconds(120), ct);
        if (!response.IsSuccessStatusCode)
        {
            var code = response.Content.Headers.ContentLength is <= 2048
                ? (await response.Content.ReadFromJsonAsync<RuntimeSwitchError>(ct))?.Error : null;
            throw new ProviderReplacementRefusedException(code switch
            {
                "source_not_at_safe_point" => "Finish the current turn, clear draft input and menus, close shell terminals and resolve approvals before retrying this replacement.",
                "source_stop_unconfirmed" => "The source CLI has not confirmed a graceful stop and final usage/state checkpoint. No target is started; retry checks the same stop without forcing it.",
                "source_archive_unavailable" => "The source native archive is unavailable. Restore state storage before retrying; the source has not been stopped.",
                "provider_state_upload_failed" => "The native state checkpoint could not be saved. Restore state storage, then retry the same replacement.",
                "handover_not_confirmed" => "The target native handover has not been confirmed. Retry checks the same target; it does not replay input.",
                _ => "The runtime refused replacement admission or checkpoint. Retry the same replacement after resolving the runtime state."
            });
        }
        return await response.Content.ReadFromJsonAsync<RuntimeSwitchState>(ct) ?? throw new ProviderReplacementRefusedException("Runtime confirmation is missing.");
    }

    private async Task<string> ReadTargetTranscriptAsync(SessionRecord record, V1Pod pod, CancellationToken ct, bool tail = false)
    {
        var uri = new UriBuilder("http", pod.Status.PodIP, _opts.AgentPort, "agenthub/transcript");
        long offset = 0;
        if (tail)
        {
            // Probe the frozen native file directly: a best-effort periodic artifact may be stale.
            // The explicit history boundary accounts for records omitted by this bounded tail.
            uri.Query = "from=999999999999999";
            using var probe = new HttpRequestMessage(HttpMethod.Get, uri.Uri);
            probe.Headers.Add("X-Agent-Token", record.CallbackToken);
            using var sizeResponse = await _podRequests!.SendAsync(record.Id, probe, HttpCompletionOption.ResponseHeadersRead, TimeSpan.FromSeconds(10), ct);
            sizeResponse.EnsureSuccessStatusCode();
            if (!sizeResponse.Headers.TryGetValues("X-Transcript-Size", out var sizes) || !long.TryParse(sizes.SingleOrDefault(), out var size) || size < 0)
                throw new ProviderReplacementRefusedException("Source native transcript size is unavailable.");
            offset = Math.Max(0, size - 750_000);
            uri.Query = "from=" + offset;
        }
        using var request = new HttpRequestMessage(HttpMethod.Get, uri.Uri);
        request.Headers.Add("X-Agent-Token", record.CallbackToken);
        using var response = await _podRequests!.SendAsync(record.Id, request, HttpCompletionOption.ResponseHeadersRead, TimeSpan.FromSeconds(10), ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        var buffer = new char[1_000_001]; var length = 0;
        while (length < buffer.Length)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(length), ct);
            if (count == 0) break;
            length += count;
        }
        if (length > 1_000_000) throw new ProviderReplacementRefusedException("Target handover evidence exceeds the checkpoint bound.");
        var text = new string(buffer, 0, length);
        if (offset > 0)
        {
            var firstNewline = text.IndexOf('\n');
            text = firstNewline < 0 ? "" : text[(firstNewline + 1)..];
        }
        return text;
    }

    private static IReadOnlyList<TranscriptEntry> RetainedSource(IReadOnlyList<TranscriptEntry> entries, long generation, AgentKind agent)
    {
        var retained = new List<TranscriptEntry>(); var chars = 0;
        foreach (var entry in entries.Reverse())
        {
            var size = JsonSerializer.Serialize(entry with { Generation = generation, Agent = agent.ToString() }).Length;
            if (retained.Count >= 4998 || chars + size > 990_000) break;
            retained.Add(entry); chars += size;
        }
        retained.Reverse();
        retained.Insert(0, new("boundary", "Source checkpoint includes only retained native history. Earlier or unavailable records and raw native state are omitted; historical coverage is unknown.", null));
        return retained;
    }

    private static CreateSessionRequest ReplacementSessionRequest(SessionRecord record) => new()
    {
        Title = record.Title, Description = record.Description, Mode = record.Mode, UiMode = record.UiMode,
        Repos = ParseRepos(record), ProjectId = record.ProjectId,
        GitPatIds = GitPatSelection.Parse(record.GitPatIdsJson)?.ToList(),
        SecretIds = SessionSecretSelection.Parse(record.SecretIdsJson)?.ToList(),
        Agent = record.Agent, AuthMode = record.AuthMode, CredentialId = record.CredentialId,
        ModelEndpointUrl = record.ModelEndpointUrl, ModelName = record.ModelName,
        Cpu = record.Cpu, Memory = record.Memory, CpuLimit = record.CpuLimit, MemoryLimit = record.MemoryLimit,
        AutoApprove = record.AutoApprove, SystemPrompt = record.SystemPrompt,
        Policy = ParsePolicy(record), AllowedTools = ParseAllowedTools(record),
        McpConfigJson = record.McpConfigJson,
        McpServerIds = ParseMcpServerIds(record)
    };
}

public static class ProviderSwitchCredentials
{
    public static string? SelectAccount(ProviderAccountSet accounts, string? requested, DateTime now)
    {
        bool Usable(ProviderAccount account) => account.LoginRejectedAt is null && !ProviderAccountSecret.IsExhausted(account, now);
        if (requested is not null) return accounts.Find(requested) is { } pinned && Usable(pinned) ? pinned.Id : null;
        if (accounts.Default is { } preferred && Usable(preferred)) return preferred.Id;
        return accounts.Accounts.FirstOrDefault(Usable)?.Id;
    }
}
