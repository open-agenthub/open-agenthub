using System.Security.Cryptography;
using System.Text;
using AgentHub.Api.Persistence;

namespace AgentHub.Api.Services;

public interface IExecutionConversationSource
{
    Task<IReadOnlyList<TranscriptEntry>> ComposeExecutionHistoryAsync(string owner, string id,
        IReadOnlyList<TranscriptEntry> current, CancellationToken ct = default);
}

public static class ExecutionConversation
{
    public static IReadOnlyList<TranscriptEntry> Compose(ExecutionHistory history, IReadOnlyList<TranscriptEntry> current)
    {
        var generation = history.Current.Generation;
        var boundary = new TranscriptEntry("boundary", $"Provider execution: {history.Current.Agent}", null, $"execution:{generation}")
            { Generation = generation, Agent = history.Current.Agent.ToString() };
        var all = history.Previous.Concat([boundary]).Concat(current.Select(e => e with
            { Generation = generation, Agent = history.Current.Agent.ToString() })).ToList();
        var chars = 0; var start = all.Count; var count = 0;
        while (start > 0 && count < PostgresSessionExecutionStore.MaxHistoryEntries)
        {
            var entry = all[start - 1];
            var length = entry.Text.Length + (entry.Tool?.Length ?? 0) + 100;
            if (chars + length > PostgresSessionExecutionStore.MaxHistoryChars) break;
            chars += length; start--; count++;
        }
        var retained = all.Skip(start).ToList();
        if (start > 0 || history.Omitted)
            retained.Insert(0, new TranscriptEntry("boundary", "Earlier retained history omitted from this bounded view.", null));
        // Native projection changes can replace earlier items without shrinking the timeline.
        // Carry the active source even when the first item belongs to a sealed execution.
        var source = current.FirstOrDefault()?.SourceId ?? "stored";
        var revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{generation}:{history.Current.NativeId}:{source}:{history.Previous.Count}:{start}:{history.Omitted}")));
        if (retained.Count > 0) retained[0] = retained[0] with { SourceId = "execution:" + revision };
        return retained;
    }
}
