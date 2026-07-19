using System.Collections.Concurrent;

namespace AgentHub.Api.Chat;

/// <summary>
/// In-memory "Claude is working…" animator: per session one loop that invokes an
/// edit callback with the next frame until stopped or maxDuration elapses. The
/// message itself (creation/deletion) is owned by the platform adapters; deletion
/// works cross-replica via the persisted status message ref.
/// </summary>
public sealed class WorkingIndicator
{
    public static readonly string[] Frames =
        { "⏳ Claude is working …", "⌛ Claude is working ‥", "⏳ Claude is working .", "⌛ Claude is working ‥" };

    private readonly TimeSpan _interval, _maxDuration;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _loops = new();

    public WorkingIndicator() : this(TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(30)) { }
    public WorkingIndicator(TimeSpan interval, TimeSpan maxDuration) { _interval = interval; _maxDuration = maxDuration; }

    public void Start(string sessionId, Func<string, CancellationToken, Task> edit)
    {
        Stop(sessionId);
        var cts = new CancellationTokenSource(_maxDuration);
        _loops[sessionId] = cts;
        _ = Task.Run(async () =>
        {
            var i = 0;
            try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    await Task.Delay(_interval, cts.Token);
                    await edit(Frames[++i % Frames.Length], cts.Token);
                }
            }
            catch { /* cancelled or edit failed — stop quietly */ }
        });
    }

    public void Stop(string sessionId)
    {
        if (_loops.TryRemove(sessionId, out var cts)) { cts.Cancel(); cts.Dispose(); }
    }
}
