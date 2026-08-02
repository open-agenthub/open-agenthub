using AgentHub.Api.Models;
using AgentHub.Api.Services;
using AgentHub.Api.Storage;

namespace AgentHub.Api.Files;

public interface ISessionFileCleanup
{
    Task DeleteSessionAsync(
        string sessionId,
        SessionInfo? liveSession,
        CancellationToken ct = default);
    Task DeleteExpiredAsync(
        SessionFileRecord file,
        SessionInfo? liveSession,
        CancellationToken ct = default);
}

public sealed class SessionFileCleanup(
    ISessionFileRegistry registry,
    IArtifactStore artifacts,
    IAgentFileClient agentFiles,
    ILogger<SessionFileCleanup> logger) : ISessionFileCleanup
{
    public async Task DeleteSessionAsync(
        string sessionId,
        SessionInfo? liveSession,
        CancellationToken ct = default)
    {
        var files = await registry.ListAsync(sessionId, ct);
        foreach (var file in files)
        {
            try
            {
                await DeleteContentAsync(file, liveSession, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception,
                    "Could not remove content for session file {FileId}", file.Id);
            }
        }

        await registry.MarkSessionDeletedAsync(sessionId, ct);
    }

    public async Task DeleteExpiredAsync(
        SessionFileRecord file,
        SessionInfo? liveSession,
        CancellationToken ct = default)
    {
        try
        {
            await DeleteContentAsync(file, liveSession, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception,
                "Could not remove content for expired session file {FileId}", file.Id);
        }

        await registry.TransitionAsync(file.SessionId, file.Id,
            SessionFileState.Expired, SessionFileState.Deleted, null, null, ct);
    }

    private async Task DeleteContentAsync(
        SessionFileRecord file,
        SessionInfo? liveSession,
        CancellationToken ct)
    {
        if (file.StorageKind == SessionFileStorageKind.S3)
        {
            await artifacts.DeleteAsync(file.StorageLocator, ct);
        }
        else if (liveSession is { Phase: "Running", PodIp: not null })
        {
            await agentFiles.DeleteAsync(liveSession, file, ct);
        }
    }
}

public sealed class SessionFileSweepService(
    ISessionFileRegistry registry,
    ISessionFileCleanup cleanup,
    ISessionService sessions,
    SessionFileOptions options,
    ILogger<SessionFileSweepService> logger) : BackgroundService
{
    internal async Task SweepOnceAsync(CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-options.ReservationMinutes);
        var expired = await registry.ExpireReservationsAsync(cutoff, ct);
        foreach (var file in expired)
        {
            SessionInfo? liveSession = null;
            if (file.StorageKind == SessionFileStorageKind.Pod)
            {
                try
                {
                    liveSession = await sessions.GetSessionAsync(file.Owner, file.SessionId, ct);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogWarning(exception,
                        "Could not resolve session for expired file {FileId}", file.Id);
                }
            }
            await cleanup.DeleteExpiredAsync(file, liveSession, ct);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(
            TimeSpan.FromSeconds(Math.Max(5, options.SweepSeconds)));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await SweepOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Session file reservation sweep failed");
            }
        }
    }
}
