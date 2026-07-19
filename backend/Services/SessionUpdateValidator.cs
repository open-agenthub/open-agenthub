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

        AgentConfiguration.ValidateForUpdate(
            record.Agent, record.AuthMode, request.Agent, request.AuthMode);
    }

    private static bool HasRuntimeField(UpdateSessionRequest request) =>
        request.Image is not null ||
        request.RunAsRoot is not null ||
        request.Cpu is not null ||
        request.Memory is not null ||
        request.McpConfigJson is not null ||
        request.Repos is not null ||
        request.Agent is not null ||
        request.AuthMode is not null ||
        request.Policy is not null;
}
