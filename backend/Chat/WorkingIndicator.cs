using System.Collections.Concurrent;

namespace AgentHub.Api.Chat;

/// <summary>
/// In-memory "Claude is working…" animator: per session one loop that invokes an
/// edit callback with the next frame until stopped, the edit is rejected (message
/// gone), or maxDuration elapses. The message itself (creation/deletion) is owned
/// by the platform adapters; deletion works cross-replica via the persisted status
/// message ref.
/// </summary>
public sealed class WorkingIndicator
{
    public static IReadOnlyList<string> Frames { get; } =
        new[] { "⏳ Claude is working …", "⌛ Claude is working ‥", "⏳ Claude is working .", "⌛ Claude is working ‥" };

    private readonly TimeSpan _interval, _maxDuration;
    private readonly ILogger<WorkingIndicator>? _log;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _loops = new();

    public WorkingIndicator(ILogger<WorkingIndicator>? log = null)
        : this(TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(30), log) { }

    public WorkingIndicator(TimeSpan interval, TimeSpan maxDuration, ILogger<WorkingIndicator>? log = null)
    { _interval = interval; _maxDuration = maxDuration; _log = log; }

    /// <summary>
    /// Starts (or restarts) the animation loop for a session. The edit callback
    /// returns false when the platform rejected the edit (e.g. the status message
    /// was deleted by another replica) — the loop then exits on its own.
    /// </summary>
    public void Start(string sessionId, Func<string, CancellationToken, Task<bool>> edit)
    {
        if (_loops.TryRemove(sessionId, out var prev)) Cancel(prev);
        var cts = new CancellationTokenSource(_maxDuration);
        if (!_loops.TryAdd(sessionId, cts)) { cts.Cancel(); cts.Dispose(); return; } // concurrent Start won — back off
        var token = cts.Token;
        _ = Task.Run(async () =>
        {
            var i = 0;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(_interval, token);
                    token.ThrowIfCancellationRequested();
                    if (!await edit(Frames[++i % Frames.Count], token)) break;
                }
            }
            catch (OperationCanceledException) { /* stopped or maxDuration elapsed */ }
            catch (Exception ex) { _log?.LogWarning(ex, "Working indicator loop for session {SessionId} stopped on error", sessionId); }
            finally
            {
                // Self-remove only our own pair (a newer loop may have replaced us),
                // then dispose: the loop is the CTS's single disposer.
                ((ICollection<KeyValuePair<string, CancellationTokenSource>>)_loops).Remove(new(sessionId, cts));
                cts.Dispose();
            }
        });
    }

    public void Stop(string sessionId)
    {
        if (_loops.TryRemove(sessionId, out var cts)) Cancel(cts);
    }

    private static void Cancel(CancellationTokenSource cts)
    {
        try { cts.Cancel(); }
        catch (ObjectDisposedException) { /* loop already finished and disposed it */ }
    }
}
