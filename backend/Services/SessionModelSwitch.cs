using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;

namespace AgentHub.Api.Services;

/// <summary>
/// How a session's model changes while it runs (#478, docs/design/live-model-switch.md), as the
/// pinned CLIs were found to behave: <c>live</c> in the running conversation, or <c>restart</c>
/// stored and taken by the next start, which resumes the conversation on it.
/// </summary>
public static class ModelSwitchPolicy
{
    public const string Live = "live";
    public const string Restart = "restart";
    public const string Unsupported = "unsupported";

    public static string ModeFor(SessionRecord session)
    {
        // An autonomous run has no conversation waiting for its next turn, and a scheduled run's
        // pod template is fixed (SessionUpdateValidator).
        if (session.Mode != SessionMode.Interactive) return Unsupported;
        return session.Agent switch
        {
            // Chat mode runs Claude Code in print mode, where there is no /model to type; the
            // next start passes the stored model and resumes.
            AgentKind.Claude => session.UiMode == SessionUiMode.Terminal ? Live : Restart,
            // Codex refuses ids outside its own list, which for an endpoint is the bundled one.
            AgentKind.Codex => session.AuthMode == AgentAuthMode.CustomEndpoint ? Restart : Live,
            AgentKind.OpenCode => Live,
            AgentKind.OpenClaw => Live,
            // Its picker could not be driven without a real login; --model with --resume could.
            AgentKind.Cursor => Restart,
            _ => Unsupported
        };
    }

    /// <summary>
    /// Whether a model the session runs on may be stored for its next start. OpenClaw takes no model
    /// at start (AgentConfiguration.SupportsModelChoice); its live switch is kept by OpenClaw's own
    /// session instead, which a restart resumes.
    /// </summary>
    public static bool StoresModel(SessionRecord session) =>
        session.AuthMode == AgentAuthMode.CustomEndpoint
            ? AgentConfiguration.SupportsCustomEndpoint(session.Agent)
            : AgentConfiguration.SupportsModelChoice(session.Agent);

    /// <summary>
    /// When the session is not running, a live-capable agent falls back to its next start if that
    /// start would put the conversation on the stored model. OpenCode's resume keeps the model the
    /// conversation last used, and OpenClaw takes none at start, so those two switch live only.
    /// </summary>
    public static bool RestartApplies(SessionRecord session) =>
        StoresModel(session) && session.Agent is not AgentKind.OpenCode;
}

public sealed record ModelSwitchRequest(string? Model);

public sealed record ModelSwitchOutcome
{
    public required string Model { get; init; }
    /// <summary><c>live</c>: the running CLI confirmed it. <c>restart</c>: stored for the next start.</summary>
    public required string Mode { get; init; }
    /// <summary>The CLI's own confirmation line, for a live switch.</summary>
    public string? Confirmation { get; init; }
}

/// <summary>A switch the pod or the session's state refused, with the reason the Workspace shows.</summary>
public sealed class ModelSwitchRefusedException(int status, string code, string? detail = null) : Exception(code)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public string? Detail { get; } = detail;
}

public interface ISessionModelSwitch
{
    Task<ModelSwitchOutcome> SwitchAsync(SessionRecord session, string? model, CancellationToken ct);
}

public sealed class SessionModelSwitch(IAgentPodRequests pods, ISessionService sessions, ISessionStore store,
    IConfiguration configuration, ILogger<SessionModelSwitch>? log = null, ISessionExpiryLock? lifecycle = null) : ISessionModelSwitch
{
    // The pod waits up to 15 s for the CLI's confirmation after typing, and up to 5 s for the safe
    // point; OpenCode's picker adds its own few seconds. Its answer has to arrive before this.
    private static readonly TimeSpan PodTimeout = TimeSpan.FromSeconds(35);

    public async Task<ModelSwitchOutcome> SwitchAsync(SessionRecord session, string? model, CancellationToken ct)
    {
        await using var held = lifecycle is null ? null : await lifecycle.TryAcquireAsync(session.Id, ct);
        if (lifecycle is not null && held is null) throw new ModelSwitchRefusedException(409, "session_maintenance_pending");
        if (lifecycle is not null) session = await store.GetByIdAsync(session.Id, ct) ?? throw new ModelSwitchRefusedException(409, "session_not_found");
        if (session.ProviderSwitchPending) throw new ModelSwitchRefusedException(409, "provider_switch_pending");
        model = model?.Trim();
        if (string.IsNullOrEmpty(model) || !AgentConfiguration.IsValidModelName(model))
            throw new ModelSwitchRefusedException(400, "invalid_model");
        var mode = ModelSwitchPolicy.ModeFor(session);
        if (mode == ModelSwitchPolicy.Unsupported) throw new ModelSwitchRefusedException(400, "model_switch_unsupported");

        var live = await sessions.GetSessionAsync(session.Owner, session.Id, ct);
        var running = live?.Phase == SessionStatus.Running && IPAddress.TryParse(live.PodIp, out _);
        if (mode == ModelSwitchPolicy.Live && !running)
        {
            if (!ModelSwitchPolicy.RestartApplies(session)) throw new ModelSwitchRefusedException(409, "session_not_running");
            mode = ModelSwitchPolicy.Restart;
        }

        if (mode == ModelSwitchPolicy.Restart)
        {
            await store.SetModelNameAsync(session.Id, model, ct);
            return new ModelSwitchOutcome { Model = model, Mode = ModelSwitchPolicy.Restart };
        }

        var confirmation = await SwitchInPodAsync(session, live!.PodIp!, model, ct);
        // Stored once the CLI said so, so the next start resumes on it: Claude Code and Codex
        // would otherwise go back to the model the session started on.
        if (ModelSwitchPolicy.StoresModel(session)) await store.SetModelNameAsync(session.Id, model, ct);
        return new ModelSwitchOutcome { Model = model, Mode = ModelSwitchPolicy.Live, Confirmation = confirmation };
    }

    private async Task<string?> SwitchInPodAsync(SessionRecord session, string podIp, string model, CancellationToken ct)
    {
        var uri = new UriBuilder("http", podIp, configuration.GetValue("AgentHub:AgentPort", 7681), "agenthub/model").Uri;
        using var request = new HttpRequestMessage(HttpMethod.Post, uri);
        request.Headers.Add("X-Agent-Token", session.CallbackToken);
        request.Content = JsonContent.Create(new { model });
        HttpResponseMessage response;
        try
        {
            response = await pods.SendAsync(session.Id, request, HttpCompletionOption.ResponseContentRead, PodTimeout, ct);
        }
        catch (AgentPodNotConfirmedException)
        {
            throw new ModelSwitchRefusedException(409, "session_not_running");
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            throw new ModelSwitchRefusedException(502, "model_switch_unavailable");
        }
        using (response)
        {
            var body = await ReadBodyAsync(response, ct);
            if (response.IsSuccessStatusCode) return Text(body, "confirmation");
            // An older runtime has no route; it answers with the file handler's 404.
            if (response.StatusCode == HttpStatusCode.NotFound) throw new ModelSwitchRefusedException(409, "runtime_too_old");
            var code = Text(body, "error") is { } error && IsCode(error) ? error : "model_switch_failed";
            log?.LogInformation("Model switch for session {Session} refused by the pod: {Code}", session.Id, code);
            var status = (int)response.StatusCode is 400 or 409 or 422 or 504 ? (int)response.StatusCode : 502;
            throw new ModelSwitchRefusedException(status, code, Text(body, "detail"));
        }
    }

    private static async Task<JsonElement?> ReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            if (text.Length > 4096) return null;
            return JsonDocument.Parse(text).RootElement.Clone();
        }
        catch (JsonException) { return null; }
    }

    // What the pod sends is shown in the Workspace; only a bounded single line goes through.
    private static string? Text(JsonElement? body, string name)
    {
        if (body is not { ValueKind: JsonValueKind.Object } root || !root.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String) return null;
        var text = new string(value.GetString()!.Where(c => !char.IsControl(c)).Take(200).ToArray()).Trim();
        return text.Length == 0 ? null : text;
    }

    private static bool IsCode(string value) => value.Length <= 64 && value.All(c => c is (>= 'a' and <= 'z') or '_');
}
