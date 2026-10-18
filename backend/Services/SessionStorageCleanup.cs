using System.Text.Json;
using AgentHub.Api.Persistence;
using AgentHub.Api.Storage;
using Microsoft.AspNetCore.DataProtection;

namespace AgentHub.Api.Services;

public sealed class SessionStorageCleanup(
    IArtifactStore artifacts, ISessionStore sessions, ISessionExpiryLock locks,
    IDataProtectionProvider protection, ILogger<SessionStorageCleanup> logger)
{
    public const int PlanLimit = 500;
    public static readonly TimeSpan PlanLifetime = TimeSpan.FromMinutes(15);
    public sealed record Plan(DateTime CreatedAt, string[] Prefixes);
    public sealed record Preview(int Scanned, int Existing, string[] Prefixes, string Token, bool LimitReached);
    public sealed record Result(int Candidates, int Deleted, int Skipped, int Failed);

    public static async Task DeleteAsync(IArtifactStore store, string owner, string id, CancellationToken ct)
    {
        // A missing configuration can be an outage or a removed secret, not proof that
        // this session never uploaded data. Preserve the row until storage is restored.
        if (!store.IsConfigured) throw new InvalidOperationException("Object storage must be configured before deleting a session.");
        foreach (var prefix in new[] {
            $"sessions/{KubernetesSessionService.OwnerKey(owner)}/{id}/",
            $"sessions/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(id)}/" }.Distinct())
            await store.DeleteByPrefixAsync(prefix, ct);
    }

    private IDataProtector Protector(string admin) => protection.CreateProtector("session-storage-cleanup-v1", admin);

    public async Task<Preview> PreviewAsync(string admin, CancellationToken ct)
    {
        if (!artifacts.IsConfigured) throw new InvalidOperationException("Object storage is not configured.");
        var prefixes = new HashSet<string>(StringComparer.Ordinal);
        var scanned = 0;
        var existing = 0;
        await foreach (var prefix in artifacts.ListSessionPrefixesAsync(ct))
        {
            scanned++;
            var id = SessionId(prefix);
            if (id is null || await sessions.GetByIdAsync(id, ct) is not null) { existing++; continue; }
            prefixes.Add(prefix);
            if (prefixes.Count == PlanLimit) break;
        }
        var plan = new Plan(DateTime.UtcNow, prefixes.ToArray());
        logger.LogInformation("Session storage dry run: {Scanned} scanned, {Existing} protected, {Candidates} orphan prefixes",
            scanned, existing, plan.Prefixes.Length);
        return new Preview(scanned, existing, plan.Prefixes,
            Protector(admin).Protect(JsonSerializer.Serialize(plan)), prefixes.Count == PlanLimit);
    }

    public async Task<Result> ApplyAsync(string admin, string token, CancellationToken ct)
    {
        // A protected, owner-bound preview is required; an apply cannot expand the reviewed set.
        var plan = JsonSerializer.Deserialize<Plan>(Protector(admin).Unprotect(token))
            ?? throw new ArgumentException("Invalid cleanup plan.");
        if (plan.CreatedAt > DateTime.UtcNow || DateTime.UtcNow - plan.CreatedAt > PlanLifetime ||
            plan.Prefixes.Length > PlanLimit)
            throw new ArgumentException("Cleanup plan expired; run a new dry run.");
        if (!artifacts.IsConfigured) throw new InvalidOperationException("Object storage is not configured.");
        var deleted = 0;
        var skipped = 0;
        var failed = 0;
        foreach (var prefix in plan.Prefixes.Distinct())
        {
            ct.ThrowIfCancellationRequested();
            var id = SessionId(prefix) ?? throw new ArgumentException("Invalid session prefix.");
            await using var held = await locks.TryAcquireAsync(id, ct);
            // Check by id across ALL owners, under the same lock as session maintenance.
            // A stale preview must never authorize deleting an existing session's data.
            if (held is null || await sessions.GetByIdAsync(id, ct) is not null) { skipped++; continue; }
            try
            {
                await artifacts.DeleteByPrefixAsync(prefix, ct);
                deleted++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                failed++;
                logger.LogWarning(ex, "Orphan session storage deletion failed; rerun the dry run to retry");
            }
        }
        logger.LogInformation("Session storage cleanup: {Candidates} candidates, {Deleted} deleted, {Skipped} skipped, {Failed} failed",
            plan.Prefixes.Length, deleted, skipped, failed);
        return new Result(plan.Prefixes.Length, deleted, skipped, failed);
    }

    public static string? SessionId(string prefix)
    {
        var parts = prefix.Split('/');
        if (parts.Length != 4 || parts[0] != "sessions" || parts[1].Length == 0 ||
            parts[2].Length == 0 || parts[3].Length != 0) return null;
        var id = Uri.UnescapeDataString(parts[2]);
        return id is "." or ".." || id.Contains('/') || id.Contains('\\') ? null : id;
    }
}
