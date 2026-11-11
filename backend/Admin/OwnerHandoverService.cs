using System.Text.Json;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using AgentHub.Api.Storage;
using Microsoft.AspNetCore.DataProtection;

namespace AgentHub.Api.Admin;

/// <summary>Object-storage data of the sessions that move.</summary>
public sealed record HandoverStorage(bool Configured, long Objects, long Bytes);

/// <summary>What a handover would do, for the admin to confirm. <see cref="Token"/> fixes the counts;
/// it is absent while <see cref="LiveSessions"/> blocks the handover.</summary>
public sealed record HandoverPreview(
    string From, string To, bool IncludeCredentials,
    IReadOnlyDictionary<string, long> Moves, IReadOnlyDictionary<string, long> Stays,
    IReadOnlyList<HandoverRename> Renames, IReadOnlyList<CredentialKindPreview> Credentials,
    HandoverStorage Storage, long ScheduledSessions, IReadOnlyList<string> LiveSessions, string? Token);

/// <summary>The outcome of an apply. <see cref="CleanupPending"/>: the data is the target's, but removing
/// the source's copies or rebuilding a schedule failed; the next preview or apply finishes it.</summary>
public sealed record HandoverResult(HandoverAuditEntry Audit, bool CleanupPending);

/// <summary>A request the handover refuses, with the HTTP status the controller answers.</summary>
public sealed class HandoverRefusedException(int status, string code, string message, IReadOnlyList<string>? sessions = null)
    : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public IReadOnlyList<string> Sessions { get; } = sessions ?? [];
}

/// <summary>
/// Hands the data of an owner from API-key mode to a chosen OIDC user (#501,
/// docs/apikey-handover.md). Preview, then apply with the preview's token.
///
/// The order is what makes a failure safe: everything before the database switch only adds copies
/// (credentials into the target's secrets, objects under the target's prefixes) and leaves the
/// source untouched; the switch itself is one Postgres transaction; only after it commits are the
/// source's copies removed. A failure before the commit therefore changes no ownership, and one after
/// it leaves a <c>committed</c> audit row whose remaining steps the next call finishes.
/// </summary>
public sealed class OwnerHandoverService(
    OwnerHandoverStore store, ISingleUserOwnerStore singleUserOwners, UserDirectory users,
    IArtifactStore artifacts, IDataProtectionProvider protection, Auth.HubAuthOptions? hubAuth,
    ILogger<OwnerHandoverService> log, IOwnerHandoverRuntime? runtime = null)
{
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(15);
    private sealed record Plan(string From, string To, bool Credentials, Dictionary<string, long> Moves, DateTime CreatedAt);

    private IDataProtector Protector(string admin) => protection.CreateProtector("owner-handover-v1", admin);

    /// <summary>The names data can come from, and the users it can go to.</summary>
    public async Task<(IReadOnlyList<SingleUserOwnerData> Sources, IReadOnlyList<AdminUser> Targets)> CandidatesAsync(CancellationToken ct)
    {
        var reserved = await singleUserOwners.ListOwnersAsync(ct);
        var targets = (await users.ListAsync(ct)).Where(u => !reserved.Contains(u.Owner, StringComparer.OrdinalIgnoreCase)).ToList();
        return (await singleUserOwners.ListWithDataAsync(ct), targets);
    }

    public async Task<HandoverPreview> PreviewAsync(string admin, string from, string to, bool includeCredentials, CancellationToken ct)
    {
        await FinishPendingAsync(ct);
        (from, to) = await ValidateAsync(from, to, ct);
        var inventory = await store.InventoryAsync(from, to, ct);
        var live = runtime is null ? [] : await runtime.LiveSessionIdsAsync(from, ct);
        var credentials = runtime is null ? [] : await runtime.CredentialPreviewAsync(from, to, ct);
        var storage = await MeasureAsync(from, inventory.Sessions, ct);
        var token = live.Count > 0 ? null : Protector(admin).Protect(JsonSerializer.Serialize(
            new Plan(from, to, includeCredentials, new Dictionary<string, long>(inventory.Moves), DateTime.UtcNow)));
        return new HandoverPreview(from, to, includeCredentials, inventory.Moves, inventory.Stays, inventory.Renames,
            credentials, storage, inventory.Sessions.Count(s => s.Mode == "Scheduled"), live, token);
    }

    public async Task<HandoverResult> ApplyAsync(string admin, string token, CancellationToken ct)
    {
        Plan plan;
        try { plan = JsonSerializer.Deserialize<Plan>(Protector(admin).Unprotect(token)) ?? throw new FormatException(); }
        catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or FormatException or JsonException)
        {
            // Bound to the admin who previewed: another admin's token, or a forged one, is not a plan.
            throw new HandoverRefusedException(400, "invalid_token", "The preview token is not valid. Preview again.");
        }
        if (DateTime.UtcNow - plan.CreatedAt > TokenLifetime || plan.CreatedAt > DateTime.UtcNow.AddMinutes(1))
            throw new HandoverRefusedException(409, "preview_expired", "The preview expired. Preview again.");

        await FinishPendingAsync(ct);
        var (from, to) = await ValidateAsync(plan.From, plan.To, ct);
        await RefuseLiveAsync(from, ct);
        var inventory = await store.InventoryAsync(from, to, ct);
        if (inventory.Moves.Count != plan.Moves.Count || inventory.Moves.Any(kv => plan.Moves.GetValueOrDefault(kv.Key, -1) != kv.Value))
            throw new HandoverRefusedException(409, "preview_stale", new HandoverStaleException().Message);
        if (inventory.Moves.Values.All(v => v == 0) && !plan.Credentials)
            throw new HandoverRefusedException(409, "nothing_to_move", $"{from} owns nothing to hand over.");

        var scheduled = inventory.Sessions.Where(s => s.Mode == "Scheduled").Select(s => s.Id).ToList();
        log.LogInformation("Owner handover {From} -> {To} started by {Admin}: {Sessions} sessions, credentials {Credentials}",
            from, to, admin, inventory.Sessions.Count, plan.Credentials);
        // Suspended for the whole move: a run starting in between would mount the source's secrets
        // and write to the source's prefix after its objects were copied.
        if (runtime is not null && scheduled.Count > 0) await runtime.SuspendSchedulesAsync(scheduled, true, ct);
        HandoverAuditEntry entry;
        try
        {
            if (plan.Credentials && runtime is not null) await runtime.CopyCredentialsAsync(from, to, ct);
            await CopyObjectsAsync(from, to, inventory.Sessions, ct);
            // Checked again right before the switch: a session resumed during the copy would keep
            // writing to the prefix the cleanup removes.
            await RefuseLiveAsync(from, ct);
            entry = await store.ApplyAsync(admin, from, to, plan.Credentials, plan.Moves, ct);
        }
        catch (HandoverStaleException e)
        {
            await RestoreSchedulesAsync(scheduled);
            throw new HandoverRefusedException(409, "preview_stale", e.Message);
        }
        catch
        {
            await RestoreSchedulesAsync(scheduled);
            throw;
        }
        log.LogInformation("Owner handover {Id} committed: {From} -> {To}, {Counts}", entry.Id, from, to,
            string.Join(", ", entry.Counts.Where(kv => kv.Value > 0).Select(kv => $"{kv.Key}={kv.Value}")));
        var done = await FinishAsync(entry, ct);
        return new HandoverResult(entry, !done);
    }

    public Task<IReadOnlyList<HandoverAuditEntry>> HistoryAsync(CancellationToken ct) => store.ListAsync(20, ct: ct);

    /// <summary>Finishes handovers whose database switch committed but whose cleanup did not.</summary>
    public async Task FinishPendingAsync(CancellationToken ct)
    {
        foreach (var entry in await store.ListAsync(20, pendingOnly: true, ct))
            await FinishAsync(entry, ct);
    }

    private async Task<bool> FinishAsync(HandoverAuditEntry entry, CancellationToken ct)
    {
        try
        {
            if (runtime is not null)
                foreach (var id in entry.ScheduledIds)
                {
                    try { await runtime.RebuildScheduleAsync(entry.To, id, ct); }
                    // Gone since: deleted by its new owner, nothing left to rebuild.
                    catch (KeyNotFoundException) { }
                }
            if (artifacts.IsConfigured)
                foreach (var id in entry.SessionIds)
                    foreach (var (source, _) in Prefixes(entry.From, entry.To, id))
                        await artifacts.DeleteByPrefixAsync(source, ct);
            if (entry.Credentials && runtime is not null) await runtime.DeleteCredentialsAsync(entry.From, ct);
            await store.CompleteAsync(entry.Id, ct);
            log.LogInformation("Owner handover {Id} completed", entry.Id);
            return true;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogWarning(e, "Owner handover {Id} committed, but its cleanup failed; the next preview or apply retries it", entry.Id);
            return false;
        }
    }

    private async Task RestoreSchedulesAsync(IReadOnlyCollection<string> scheduled)
    {
        if (runtime is null || scheduled.Count == 0) return;
        // Not the request's token: a cancelled request must not leave the source's schedules off.
        try { await runtime.SuspendSchedulesAsync(scheduled, false, CancellationToken.None); }
        catch (Exception e) { log.LogError(e, "Restoring schedules after a failed owner handover failed; they stay suspended"); }
    }

    private async Task<(string From, string To)> ValidateAsync(string from, string to, CancellationToken ct)
    {
        if (hubAuth?.Mode != Auth.HubAuthMode.Oidc)
            throw new HandoverRefusedException(409, "oidc_required",
                "Data is handed over to a team-login user; this install does not use OIDC sign-in.");
        from = from?.Trim() ?? ""; to = to?.Trim() ?? "";
        var reserved = await singleUserOwners.ListOwnersAsync(ct);
        var source = reserved.FirstOrDefault(o => string.Equals(o, from, StringComparison.OrdinalIgnoreCase))
            ?? throw new HandoverRefusedException(400, "invalid_source",
                "Only data from API-key or development mode can be handed over this way.");
        if (reserved.Contains(to, StringComparer.OrdinalIgnoreCase))
            throw new HandoverRefusedException(400, "invalid_target",
                "The target must be a team-login user, not a name from API-key or development mode.");
        // Ownership is by exact name, so the target is the directory's spelling of it.
        var target = (await users.ListAsync(ct)).FirstOrDefault(u => u.Owner == to)
            ?? throw new HandoverRefusedException(400, "unknown_target",
                "The target user has not signed in yet. They sign in once, then the data can be handed over.");
        return (source, target.Owner);
    }

    private async Task RefuseLiveAsync(string from, CancellationToken ct)
    {
        if (runtime is null) return;
        var live = await runtime.LiveSessionIdsAsync(from, ct);
        if (live.Count > 0)
            throw new HandoverRefusedException(409, "sessions_running",
                "Pause these sessions first; a running session keeps writing to the old owner's storage.", live);
    }

    /// <summary>The two object prefixes of a session: the owner-hash one (state, transcripts) and the
    /// escaped-name one (session files), each paired with the target's.</summary>
    private static IEnumerable<(string Source, string Target)> Prefixes(string from, string to, string id)
    {
        yield return ($"sessions/{KubernetesSessionService.OwnerKey(from)}/{id}/", $"sessions/{KubernetesSessionService.OwnerKey(to)}/{id}/");
        yield return ($"sessions/{Uri.EscapeDataString(from)}/{Uri.EscapeDataString(id)}/", $"sessions/{Uri.EscapeDataString(to)}/{Uri.EscapeDataString(id)}/");
    }

    private async Task<HandoverStorage> MeasureAsync(string from, IReadOnlyList<HandoverSession> sessions, CancellationToken ct)
    {
        if (!artifacts.IsConfigured) return new HandoverStorage(false, 0, 0);
        long objects = 0, bytes = 0;
        foreach (var session in sessions)
            foreach (var (source, _) in Prefixes(from, from, session.Id))
                await foreach (var o in artifacts.ListObjectsAsync(source, ct)) { objects++; bytes += o.Size; }
        return new HandoverStorage(true, objects, bytes);
    }

    private async Task CopyObjectsAsync(string from, string to, IReadOnlyList<HandoverSession> sessions, CancellationToken ct)
    {
        if (!artifacts.IsConfigured) return;
        long copied = 0, skipped = 0, bytes = 0;
        var lastLog = DateTime.UtcNow;
        foreach (var session in sessions)
            foreach (var (source, target) in Prefixes(from, to, session.Id))
                await foreach (var o in artifacts.ListObjectsAsync(source, ct))
                {
                    var destination = target + o.Key[source.Length..];
                    // An object already copied by an earlier attempt is not copied again, so a
                    // rerun after a failure part-way costs only what is left.
                    if ((await artifacts.HeadAsync(destination, ct))?.Size == o.Size) skipped++;
                    else { await artifacts.CopyObjectAsync(o.Key, destination, o.Size, ct); copied++; bytes += o.Size; }
                    if (DateTime.UtcNow - lastLog > TimeSpan.FromSeconds(10))
                    {
                        log.LogInformation("Owner handover copy: {Copied} objects ({Bytes} bytes) copied, {Skipped} already there",
                            copied, bytes, skipped);
                        lastLog = DateTime.UtcNow;
                    }
                }
        log.LogInformation("Owner handover copy done: {Copied} objects ({Bytes} bytes) copied, {Skipped} already there",
            copied, bytes, skipped);
    }
}
