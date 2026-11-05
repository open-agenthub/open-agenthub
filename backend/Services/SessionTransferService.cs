using AgentHub.Api.Models;
using AgentHub.Api.Persistence;

namespace AgentHub.Api.Services;

/// <summary>Where a session stands for a transfer, and what the next step would do.</summary>
public sealed record SessionTransferStatus
{
    public required string SessionId { get; init; }
    public required string Phase { get; init; }
    /// <summary>Only Claude Code's stored state holds something <c>--resume</c> can read.</summary>
    public required bool Supported { get; init; }
    /// <summary>A pod is live: taking the session local pauses it first, handing back is refused.</summary>
    public required bool Live { get; init; }
    /// <summary>The conversation id the local CLI resumes; null until the session has stored state.</summary>
    public string? ResumeId { get; init; }
}

/// <summary>The outcome of asking to take a session local.</summary>
public sealed record TakeLocalResult
{
    public required string SessionId { get; init; }
    public required string Directory { get; init; }
    /// <summary>True when nothing happened yet: the session is live, and pausing it needs a yes.</summary>
    public bool ConfirmationRequired { get; init; }
    /// <summary>What the call does (or, unconfirmed, would do), in order, for the person to read.</summary>
    public required IReadOnlyList<string> Steps { get; init; }
    public bool Paused { get; init; }
    public string? ResumeId { get; init; }
    /// <summary>Run in <see cref="Directory"/> once the files are in place.</summary>
    public string? ResumeCommand { get; init; }
    public SessionTransferLink? Link { get; init; }
}

/// <summary>A single-use link and the commands that use it, for a surface that cannot write the
/// files itself (the remote MCP endpoint, the web app).</summary>
public sealed record SessionTransferLink
{
    /// <summary>The endpoint. It does not carry the secret; see <see cref="Secret"/>.</summary>
    public required string Url { get; init; }
    /// <summary>Sent as the <c>X-AgentHub-Transfer</c> header, never in the URL: a path or query
    /// ends up in ingress and proxy access logs, and whoever reads one in time could redeem it first.</summary>
    public required string Secret { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    /// <summary>bash or zsh (Linux, macOS, WSL, Git Bash).</summary>
    public required string Command { get; init; }
    /// <summary>Windows PowerShell 5.1 and PowerShell 7.</summary>
    public required string PowerShellCommand { get; init; }
}

public sealed record HandBackResult
{
    public required string SessionId { get; init; }
    public required string ResumeId { get; init; }
    public required bool Resumed { get; init; }
    public required string Phase { get; init; }
}

public sealed record HandBackLinkResult
{
    public required string SessionId { get; init; }
    public required string Directory { get; init; }
    public required string ResumeId { get; init; }
    public required IReadOnlyList<string> Steps { get; init; }
    public required SessionTransferLink Link { get; init; }
}

/// <summary>
/// Takes a session's conversation to a workstation and back (docs/session-transfer.md). Every
/// surface — the token API the stdio MCP server uses, the remote MCP endpoint and the web app —
/// goes through this class, so the one-conversation rules are stated once:
/// the pod is stopped before the conversation leaves, and nothing is stored while a pod is live.
/// </summary>
public sealed class SessionTransferService(
    ISessionService sessions, ISessionTransferTicketStore tickets, IConfiguration cfg, ILogger<SessionTransferService> log)
{
    public static readonly TimeSpan LinkLifetime = TimeSpan.FromMinutes(15);

    public async Task<SessionTransferStatus> StatusAsync(string owner, string id, CancellationToken ct = default)
    {
        var info = await GetAsync(owner, id, ct);
        string? resumeId = null;
        if (info.Agent == AgentKind.Claude && await sessions.OpenStateArchiveAsync(owner, id, ct) is { } archive)
        {
            await using (archive)
            {
                try { resumeId = SessionLocalTransfer.ResumeId(await BufferAsync(archive, ct)); }
                // No conversation stored yet: the status reports that as a null resumeId.
                catch (SessionTransferException) { resumeId = null; }
            }
        }
        return new SessionTransferStatus
        {
            SessionId = id, Phase = info.Phase, Supported = info.Agent == AgentKind.Claude,
            Live = !SessionStatus.CanReplaceState(info.Phase), ResumeId = resumeId
        };
    }

    /// <summary>
    /// Pauses a live session (only with <paramref name="confirm"/>), waits until its pod has
    /// written its final state and is gone, and checks that a conversation is there to take.
    /// </summary>
    public async Task<TakeLocalResult> TakeLocalAsync(string owner, string id, string? directory, bool confirm,
        bool withLink, CancellationToken ct = default)
    {
        var local = SessionLocalTransfer.ValidateLocalDirectory(directory);
        var info = await SupportedAsync(owner, id, ct);
        var live = !SessionStatus.CanReplaceState(info.Phase);
        var steps = new List<string>();
        if (live) steps.Add("Pause the session: its pod saves the conversation and stops, so it is not answered in two places.");
        steps.Add($"Write the conversation to ~/.claude/projects/{SessionLocalTransfer.ProjectFolder(local)}/ for {local}.");
        steps.Add($"Continue it there with claude --resume; hand it back later, which resumes the session in the hub.");
        if (live && !confirm)
            return new TakeLocalResult
            {
                SessionId = id, Directory = local, ConfirmationRequired = true, Steps = steps
            };

        if (live)
        {
            await sessions.PauseSessionAsync(owner, id, ct);
            log.LogInformation("Paused session {Id} to take its conversation local", Auth.ApiKeyFailureLimiter.LogSafe(id));
        }
        // A pause returns once the pod is told to stop; it uploads its final state while it shuts down.
        // Read sooner, the archive misses the last turns — the ones the person just watched happen.
        try { await sessions.WaitForPodGoneAsync(owner, id, ct); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SessionTransferException("pod_still_stopping",
                "The session is paused, but its pod has not finished stopping. Try again in a minute.");
        }

        var resumeId = await StoredResumeIdAsync(owner, id, ct);
        SessionTransferLink? link = null;
        if (withLink)
        {
            var (url, secret, expires) = await MintAsync(new SessionTransferTicket(owner, id, SessionTransferDirection.TakeLocal,
                local, Resume: false, DateTimeOffset.UtcNow + LinkLifetime), ct);
            link = SessionTransferCommands.TakeLocal(url, secret, expires, local, resumeId, id);
        }
        return new TakeLocalResult
        {
            SessionId = id, Directory = local, Steps = steps, Paused = live, ResumeId = resumeId,
            ResumeCommand = SessionTransferCommands.Resume(resumeId), Link = link
        };
    }

    /// <summary>The conversation retargeted at <paramref name="directory"/>, packed relative to the home directory.</summary>
    public async Task<LocalTranscriptArchive> LocalArchiveAsync(string owner, string id, string? directory, CancellationToken ct = default)
    {
        var local = SessionLocalTransfer.ValidateLocalDirectory(directory);
        var info = await SupportedAsync(owner, id, ct);
        // Downloading next to a live pod forks the conversation: both copies take turns, and the
        // hand-back later keeps one of them. Taking it local pauses first; this refuses the rest.
        if (!SessionStatus.CanReplaceState(info.Phase))
            throw new SessionTransferException("session_live",
                "The session is running. Take it local (which pauses it) before downloading the conversation.");
        await using var archive = await sessions.OpenStateArchiveAsync(owner, id, ct) ?? throw NoState();
        return SessionLocalTransfer.ForLocal(await BufferAsync(archive, ct), local);
    }

    /// <summary>Stores a conversation continued on a workstation and, unless told otherwise, resumes the session.</summary>
    public async Task<HandBackResult> HandBackAsync(string owner, string id, Stream upload, bool resume, CancellationToken ct = default)
    {
        var info = await SupportedAsync(owner, id, ct);
        if (!SessionStatus.CanReplaceState(info.Phase))
            throw new SessionTransferException("session_live",
                "The session is running in the hub. Pause it before handing a conversation back; its pod would overwrite the upload when it stops.");
        byte[] merged;
        await using (var archive = await sessions.OpenStateArchiveAsync(owner, id, ct) ?? throw NoState())
            merged = SessionLocalTransfer.MergeFromLocal(await BufferAsync(archive, ct), await BufferAsync(upload, ct));
        var resumeId = SessionLocalTransfer.ResumeId(new MemoryStream(merged));

        try
        {
            if (!await sessions.ReplaceStateArchiveAsync(owner, id, new MemoryStream(merged), merged.Length, ct))
                throw new SessionTransferException("storage_unavailable",
                    "Object storage is not configured, so session state cannot be stored.");
        }
        catch (InvalidOperationException error)
        {
            // The pod-phase check in the service is the last word: a resume between ours and the write.
            throw new SessionTransferException("session_live", error.Message);
        }
        log.LogInformation("Stored a conversation handed back to session {Id} ({Bytes} bytes)", Auth.ApiKeyFailureLimiter.LogSafe(id), merged.Length);

        var phase = info.Phase;
        if (resume)
        {
            try { phase = (await sessions.ResumeSessionAsync(owner, id, ct)).Phase; }
            catch (Exception error) when (error is InvalidOperationException or ArgumentException)
            {
                throw new SessionTransferException("resume_failed",
                    $"The conversation is stored, but the session did not resume: {error.Message} Resume it in the hub.");
            }
        }
        return new HandBackResult { SessionId = id, ResumeId = resumeId, Resumed = resume, Phase = phase };
    }

    /// <summary>A single-use upload link and the command that packs the local conversation into it.</summary>
    public async Task<HandBackLinkResult> HandBackLinkAsync(string owner, string id, string? directory, bool resume,
        CancellationToken ct = default)
    {
        var local = SessionLocalTransfer.ValidateLocalDirectory(directory);
        var info = await SupportedAsync(owner, id, ct);
        if (!SessionStatus.CanReplaceState(info.Phase))
            throw new SessionTransferException("session_live",
                "The session is running in the hub, so a conversation continued elsewhere cannot be handed back. Pause it first.");
        var resumeId = await StoredResumeIdAsync(owner, id, ct);
        var (url, secret, expires) = await MintAsync(new SessionTransferTicket(owner, id, SessionTransferDirection.HandBack,
            local, resume, DateTimeOffset.UtcNow + LinkLifetime), ct);
        var steps = new List<string>
        {
            $"Pack ~/.claude/projects/{SessionLocalTransfer.ProjectFolder(local)}/{resumeId}.jsonl (and its folder) and upload it.",
            "The hub stores it as the session's conversation, filed back under the session's own working directory."
        };
        if (resume) steps.Add("The session then resumes in the hub with the turns taken locally.");
        steps.Add("Stop using the local copy afterwards: the hub session is the one that continues.");
        return new HandBackLinkResult
        {
            SessionId = id, Directory = local, ResumeId = resumeId, Steps = steps,
            Link = SessionTransferCommands.HandBack(url, secret, expires, local, resumeId, id)
        };
    }

    /// <summary>Redeems a link; null for anything that is not a live, unused link of that direction.</summary>
    public Task<SessionTransferTicket?> RedeemAsync(string secret, SessionTransferDirection direction, CancellationToken ct = default)
        => string.IsNullOrEmpty(secret) || secret.Length > 128
            ? Task.FromResult<SessionTransferTicket?>(null)
            : tickets.RedeemAsync(secret, direction, ct);

    // ------------------------------------------------------------------ helpers

    private async Task<SessionInfo> GetAsync(string owner, string id, CancellationToken ct)
        => await sessions.GetSessionAsync(owner, id, ct) ?? throw new KeyNotFoundException($"Session {id} not found.");

    private async Task<SessionInfo> SupportedAsync(string owner, string id, CancellationToken ct)
    {
        var info = await GetAsync(owner, id, ct);
        if (info.Agent != AgentKind.Claude)
            throw new SessionTransferException("agent_not_supported",
                $"Only Claude Code sessions can be continued locally; this one runs {info.Agent}.");
        return info;
    }

    private async Task<string> StoredResumeIdAsync(string owner, string id, CancellationToken ct)
    {
        await using var archive = await sessions.OpenStateArchiveAsync(owner, id, ct) ?? throw NoState();
        return SessionLocalTransfer.ResumeId(await BufferAsync(archive, ct));
    }

    private async Task<(string Url, string Secret, DateTimeOffset Expires)> MintAsync(SessionTransferTicket ticket, CancellationToken ct)
    {
        // The link is run by the person's own shell, so its origin is the configured one, never the
        // request's Host header: a forwarded header must not decide where a conversation is sent.
        var origin = cfg["FrontendOrigin"]?.Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(origin))
            throw new SessionTransferException("origin_not_configured",
                "FrontendOrigin is not configured on this instance, so no transfer link can be built. Use the stdio MCP server instead.");
        var secret = await tickets.MintAsync(ticket, ct);
        return ($"{origin}/api/session-transfer", secret, ticket.ExpiresAt);
    }

    private static SessionTransferException NoState() => new("transcript_missing",
        "The session has no stored conversation yet. The hub stores it when the session's pod stops, so a session that never ran has nothing to transfer.");

    /// <summary>Object storage streams are forward-only and the archive is read more than once.</summary>
    private static async Task<MemoryStream> BufferAsync(Stream source, CancellationToken ct)
    {
        var buffer = new MemoryStream();
        await source.CopyToAsync(buffer, ct);
        buffer.Position = 0;
        return buffer;
    }
}
