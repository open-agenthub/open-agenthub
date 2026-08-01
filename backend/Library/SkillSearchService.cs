namespace AgentHub.Api.Library;

/// <summary>
/// Hybrid skill search: Postgres full-text rank, blended with cosine similarity
/// when an embedding provider is configured. Indexing is best-effort — a failing
/// embedding endpoint never breaks a save, it only degrades search to FTS.
/// </summary>
public sealed class SkillSearchService
{
    public const int DefaultLimit = 10;
    public const int MaxLimit = 50;

    private readonly ISkillStore _skills;
    private readonly ISkillEmbeddingStore _embeddings;
    private readonly IEmbeddingProvider _provider;
    private readonly ILogger<SkillSearchService> _log;

    public SkillSearchService(
        ISkillStore skills,
        ISkillEmbeddingStore embeddings,
        IEmbeddingProvider provider,
        ILogger<SkillSearchService> log)
    {
        _skills = skills;
        _embeddings = embeddings;
        _provider = provider;
        _log = log;
    }

    /// <summary>Refreshes the vector for a saved skill; noop without a provider.</summary>
    public async Task IndexAsync(SkillRecord record, string content, CancellationToken ct = default)
    {
        if (!_provider.Enabled) return;
        try
        {
            var vector = await _provider.EmbedAsync(
                $"{record.Name}\n{record.Description}\n{content}", ct);
            if (vector is not null)
                await _embeddings.UpsertAsync(record.Id, _provider.Model, vector, ct);
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Could not index skill {Id} for vector search.", record.Id);
        }
    }

    public async Task RemoveAsync(string skillId, CancellationToken ct = default)
    {
        try
        {
            await _embeddings.DeleteAsync(skillId, ct);
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Could not remove skill {Id} from the vector index.", skillId);
        }
    }

    /// <summary>
    /// Searches within the given accessible records. FTS scores and cosine
    /// similarities are normalized to 0..1 and blended half/half; a skill found
    /// by only one signal keeps that component.
    /// </summary>
    public async Task<IReadOnlyList<SkillSearchHit>> SearchAsync(
        string owner, IReadOnlyList<SkillRecord> accessible, string query, int limit,
        CancellationToken ct = default)
    {
        query = query.Trim();
        if (query.Length == 0 || accessible.Count == 0) return [];
        limit = Math.Clamp(limit, 1, MaxLimit);
        var byId = accessible.ToDictionary(r => r.Id);

        var ftsHits = await _skills.SearchAsync(byId.Keys.ToList(), query, MaxLimit, ct);
        var maxRank = ftsHits.Count > 0 ? Math.Max(ftsHits.Max(h => h.Rank), 1e-9) : 1;
        var scores = ftsHits.ToDictionary(
            h => h.Record.Id,
            h => 0.5 * (h.Rank / maxRank));

        if (_provider.Enabled && await _provider.EmbedAsync(query, ct) is { } queryVector)
        {
            var vectors = await _embeddings.GetManyAsync(byId.Keys.ToList(), _provider.Model, ct);
            foreach (var (id, vector) in vectors)
            {
                var similarity = Cosine(queryVector, vector);
                if (similarity <= 0) continue;
                scores[id] = scores.GetValueOrDefault(id) + 0.5 * similarity;
            }
        }

        return scores
            .OrderByDescending(s => s.Value)
            .Take(limit)
            .Select(s =>
            {
                var r = byId[s.Key];
                return new SkillSearchHit(
                    r.Id, r.Name, r.Description, r.Owner, r.Owner == owner,
                    r.ProjectId, r.Version, Math.Round(s.Value, 4), r.UpdatedAt);
            })
            .ToList();
    }

    public static double Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0;
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            na += (double)a[i] * a[i];
            nb += (double)b[i] * b[i];
        }
        if (na == 0 || nb == 0) return 0;
        return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }
}
