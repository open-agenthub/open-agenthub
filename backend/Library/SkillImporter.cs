using AgentHub.Api.Persistence;

namespace AgentHub.Api.Library;

/// <summary>One skill as reported by an agent pod's local ~/.claude/skills scan.</summary>
public sealed record ImportedSkill(
    string Name, string? Description, string Content, IReadOnlyList<SkillFile>? Files);

public sealed record ImportSkillsRequest(IReadOnlyList<ImportedSkill>? Skills);

public sealed record ImportSkillsResult(
    IReadOnlyList<string> Created,
    IReadOnlyList<string> Updated,
    IReadOnlyList<string> Unchanged,
    IReadOnlyList<string> Skipped);

/// <summary>
/// Imports skills an agent created locally in its session (unmanaged directories
/// under ~/.claude/skills) into the hub library — scoped to the session's project,
/// or personal when the session has none. Unchanged skills are skipped so repeated
/// syncs do not pile up identical versions.
/// </summary>
public sealed class SkillImporter
{
    private readonly ISkillStore _skills;
    private readonly SkillSearchService _search;
    private readonly ILogger<SkillImporter> _log;

    public SkillImporter(ISkillStore skills, SkillSearchService search, ILogger<SkillImporter> log)
    {
        _skills = skills;
        _search = search;
        _log = log;
    }

    public async Task<ImportSkillsResult> ImportAsync(
        SessionRecord session, IReadOnlyList<ImportedSkill> skills, CancellationToken ct)
    {
        List<string> created = [], updated = [], unchanged = [], skipped = [];
        var own = await _skills.ListByOwnerAsync(session.Owner, ct);

        foreach (var skill in skills)
        {
            try
            {
                var name = LibraryValidation.ValidateSkillName(skill.Name);
                var files = LibraryValidation.ValidateSkillFiles(skill.Files) ?? [];
                var request = new SaveSkillRequest(
                    name,
                    LibraryValidation.ValidateDescription(skill.Description),
                    LibraryValidation.ValidateSkillContent(skill.Content),
                    ProjectId: session.ProjectId,
                    Comment: "Imported from the session's local skill directory.",
                    SavedBy: $"session:{session.Id}",
                    Files: files);

                var existing = own.FirstOrDefault(r =>
                    r.Name == name && r.ProjectId == session.ProjectId);
                if (existing is not null && await IsUnchangedAsync(existing, request, ct))
                {
                    unchanged.Add(name);
                    continue;
                }

                var record = existing is null
                    ? await _skills.CreateAsync(session.Owner, request, ct)
                    : await _skills.UpdateAsync(session.Owner, existing.Id, request, ct);
                (existing is null ? created : updated).Add(name);
                await _search.IndexAsync(record, request.Content, ct);
            }
            catch (ArgumentException e)
            {
                _log.LogInformation("Skipping skill import '{Name}' from session {Id}: {Reason}",
                    skill.Name, session.Id, e.Message);
                skipped.Add(skill.Name);
            }
        }
        return new ImportSkillsResult(created, updated, unchanged, skipped);
    }

    private async Task<bool> IsUnchangedAsync(
        SkillRecord existing, SaveSkillRequest request, CancellationToken ct)
    {
        if (await _skills.GetContentAsync(existing, ct) != request.Content) return false;
        var currentFiles = await _skills.GetFilesAsync(existing.Id, existing.Version, ct);
        var incoming = request.Files ?? [];
        if (currentFiles.Count != incoming.Count) return false;
        var byPath = currentFiles.ToDictionary(f => f.Path, f => f.Content, StringComparer.Ordinal);
        return incoming.All(f => byPath.TryGetValue(f.Path, out var content) && content == f.Content);
    }
}
