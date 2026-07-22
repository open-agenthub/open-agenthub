namespace AgentHub.Api.Permissions;

/// <summary>
/// Server-side safety net for permission prompts whose hook died without calling
/// /expire (e.g. the backend was down when the hook gave up): every 5 minutes,
/// pending requests older than 35 minutes are marked expired and their chat
/// prompts defused (buttons removed).
/// </summary>
public sealed class PermissionSweepService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    // Must stay comfortably above the hook's own give-up timeout
    // (AGENTHUB_PERMISSION_POLL_SECONDS, default 1740s = 29 min): a live hook always
    // expires its request first via /expire; the sweeper only catches ones whose
    // hook died before it could.
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(35);

    private readonly PermissionStore _store;
    private readonly IEnumerable<IPermissionPromptEditor> _editors;
    private readonly ILogger<PermissionSweepService> _log;

    public PermissionSweepService(PermissionStore store, IEnumerable<IPermissionPromptEditor> editors,
        ILogger<PermissionSweepService> log)
    { _store = store; _editors = editors; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, stoppingToken);
                var stale = await _store.ExpireStaleAsync(MaxAge, stoppingToken);

                // Requests without a Platform never had a chat prompt — nothing to defuse.
                foreach (var req in stale.Where(r => r.Platform is not null))
                    foreach (var editor in _editors.Where(e => e.Platform == req.Platform))
                    {
                        try { await editor.MarkExpiredAsync(req, stoppingToken); }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex) { _log.LogWarning(ex, "Defusing the expired permission prompt {Id} failed", req.Id); }
                    }

                if (stale.Count > 0)
                    _log.LogInformation("Expired {Count} stale permission request(s)", stale.Count);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.LogWarning(ex, "Permission sweep failed; retrying next cycle"); }
        }
    }
}
