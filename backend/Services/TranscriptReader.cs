namespace AgentHub.Api.Services;

/// <summary>Selects persisted terminal output and converts it to a plain-text transcript.</summary>
public static class TranscriptReader
{
    public static async Task<string?> ReadAsync(
        Func<CancellationToken, Task<string?>> readPreferred,
        Func<CancellationToken, Task<string?>> readFallback,
        CancellationToken ct = default)
    {
        var preferred = await readPreferred(ct);
        var raw = !string.IsNullOrEmpty(preferred) ? preferred : await readFallback(ct);
        return AgentTerminal.CleanTranscript(raw);
    }
}