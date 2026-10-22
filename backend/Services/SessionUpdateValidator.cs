using AgentHub.Api.Models;
using AgentHub.Api.Persistence;

namespace AgentHub.Api.Services;

/// <summary>Validates public partial updates before any session state is mutated.</summary>
public static class SessionUpdateValidator
{
    public static void Validate(SessionRecord record, UpdateSessionRequest request)
    {
        if (record.Mode == SessionMode.Scheduled && HasRuntimeField(request))
            throw new ArgumentException(
                "Scheduled sessions run from a fixed CronJob spec — delete and recreate to change runtime settings.");

        if (request.Schedule is not null)
            SessionSchedule.Normalize(record.Mode, request.Schedule);

        AgentConfiguration.ValidateForUpdate(
            record.Agent, record.AuthMode, request.Agent, request.AuthMode,
            record.OpenClawApiKeySource, request.OpenClawApiKeySource);
        if (request.Agent is not null || request.AuthMode is not null
            || request.ModelEndpointUrl is not null || request.ModelName is not null)
            ModelEndpointAfterUpdate(record, request);

        // Not a runtime field — the pod knows nothing about it — but it has a rule of its own: a
        // scheduled session can only count from its start. Checked here, before anything is
        // mutated, like the rest.
        if (request.AutoDeleteAfterSeconds is not null || request.AutoDeleteFrom is not null)
            SessionExpiry.ForUpdate(record, request.AutoDeleteAfterSeconds, request.AutoDeleteFrom);
    }

    /// <summary>
    /// The endpoint fields the record will hold after the update, validated. Judged on the record
    /// as it will be: switching to CustomEndpoint needs an endpoint in the same request unless one
    /// is stored, a switch of agent must land on one that can use it, and leaving CustomEndpoint
    /// drops the stored values instead of failing on them.
    /// </summary>
    public static (string? Url, string? Model) ModelEndpointAfterUpdate(SessionRecord record, UpdateSessionRequest request)
    {
        var agent = request.Agent ?? record.Agent;
        var authMode = request.AuthMode ?? record.AuthMode;
        var stored = authMode == AgentAuthMode.CustomEndpoint;
        return AgentConfiguration.ValidateModelEndpoint(agent, authMode,
            request.ModelEndpointUrl ?? (stored ? record.ModelEndpointUrl : null),
            request.ModelName ?? (stored ? record.ModelName : null));
    }

    // The system prompt counts as a runtime field because a CronJob bakes it into its pod
    // template; the stored record would change while every scheduled run kept the old text.
    private static bool HasRuntimeField(UpdateSessionRequest request) =>
        request.SystemPrompt is not null ||
        request.Image is not null ||
        request.RunAsRoot is not null ||
        request.Cpu is not null ||
        request.Memory is not null ||
        request.CpuLimit is not null ||
        request.MemoryLimit is not null ||
        request.McpConfigJson is not null ||
        request.McpServerIds is not null ||
        request.EphemeralApiSources is not null ||
        request.Repos is not null ||
        request.Agent is not null ||
        request.AuthMode is not null ||
        request.OpenClawApiKeySource is not null ||
        request.ModelEndpointUrl is not null ||
        request.ModelName is not null ||
        request.CredentialId is not null ||
        request.GitPatIds is not null ||
        request.SecretIds is not null ||
        request.SecretGrantIds is not null ||
        request.SecretGrants is not null ||
        request.Policy is not null;
}
