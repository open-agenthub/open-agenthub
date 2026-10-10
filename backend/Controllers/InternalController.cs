using System.Text;
using System.Text.Json;
using AgentHub.Api.Library;
using AgentHub.Api.Files;
using AgentHub.Api.Models;
using AgentHub.Api.Browser;
using AgentHub.Api.Notifications;
using AgentHub.Api.Permissions;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using AgentHub.Api.Usage;
using AgentHub.Api.Ee.Sharing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentHub.Api.Controllers;

/// <summary>
/// Endpoints called ONLY by the agent pod. No user auth; instead a per-session
/// callback token (header X-Agent-Token) is used.
/// Not routed externally via the ingress; reachable only inside the cluster.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("internal/sessions/{id}")]
public sealed class InternalController : ControllerBase
{
    private readonly ISessionStore _store;
    private readonly IEnumerable<INotifier> _notifiers;
    private readonly ISessionService _svc;
    private readonly PermissionStore _permissions;
    private readonly IEnumerable<IPermissionNotifier> _permNotifiers;
    private readonly IEnumerable<IPermissionPromptEditor> _promptEditors;
    private readonly ISessionMcpPolicyReader _shares;
    private readonly IBrowserService? _browsers;
    private readonly bool _spawnMcpEnabled;
    private readonly ILibraryAccess _library;
    private readonly IAgentCallbackAuthorizer _callbackAuthorizer;
    private readonly IUsageStore? _usage;
    private readonly ISessionMessageStore? _messages;
    private readonly ISessionMessageDelivery? _delivery;
    private readonly IAccountFailover? _accountFailover;

    public InternalController(ISessionStore store, IEnumerable<INotifier> notifiers, ISessionService svc,
        PermissionStore permissions, IEnumerable<IPermissionNotifier> permNotifiers,
        IEnumerable<IPermissionPromptEditor> promptEditors, ISessionMcpPolicyReader shares,
        ILibraryAccess library, IBrowserService? browsers = null, bool? spawnMcpEnabled = null,
        IConfiguration? configuration = null, IAgentCallbackAuthorizer? callbackAuthorizer = null,
        IUsageStore? usage = null, ISessionMessageStore? messages = null,
        ISessionMessageDelivery? delivery = null, IAccountFailover? accountFailover = null)
    {
        _store = store; _notifiers = notifiers; _svc = svc;
        _permissions = permissions; _permNotifiers = permNotifiers; _promptEditors = promptEditors; _shares = shares;
        _library = library;
        _browsers = browsers;
        _usage = usage;
        _messages = messages;
        _delivery = delivery;
        _accountFailover = accountFailover;
        _callbackAuthorizer = callbackAuthorizer ?? new AgentCallbackAuthorizer(store);
        _spawnMcpEnabled = spawnMcpEnabled
            ?? configuration?.GetValue("AgentHub:SpawnMcpEnabled", true)
            ?? true;
    }

    private async Task NotifyAllAsync(SessionRecord rec, string ev, string message, CancellationToken ct)
    {
        // Fan out in parallel: one slow platform (e.g. Telegram's ~1 msg/s chunk pacing)
        // must not delay the others or the agent hook's POST. Every notifier catches
        // its own failures internally, so WhenAll never observes an exception.
        await Task.WhenAll(_notifiers.Select(n => n.NotifyAsync(rec, ev, message, ct)));
    }

    public record StatusBody(string Status);
    public record NotifyBody(string Message, string? Event);

    private async Task<SessionRecord?> AuthAsync(string id, CancellationToken ct)
    {
        return await _callbackAuthorizer.AuthorizeAsync(Request, id, ct);
    }

    private async Task<string?> ReadProviderCredentialBodyAsync(CancellationToken ct)
    {
        if (Request.ContentLength is > ProviderCredentialValidator.MaxBytes)
            return null;

        var buffer = new byte[ProviderCredentialValidator.MaxBytes + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await Request.Body.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct);
            if (read == 0) break;
            total += read;
        }
        return total > ProviderCredentialValidator.MaxBytes ? null : Encoding.UTF8.GetString(buffer, 0, total);
    }

    [HttpPost("status")]
    public async Task<IActionResult> Status(string id, [FromBody] StatusBody body, CancellationToken ct)
    {
        var rec = await AuthAsync(id, ct);
        if (rec is null) return Unauthorized();

        await _store.UpdateStatusAsync(id, body.Status, ct);
        if (body.Status is "Succeeded" or "Failed")
        {
            await _store.SetQuestionPendingAsync(id, false, ct);
            if (_browsers is not null) await _browsers.StopAsync(id, ct);
            await NotifyAllAsync(rec, body.Status == "Succeeded" ? "finished" : "failed",
                body.Status == "Succeeded" ? "Task completed." : "Session failed.", ct);
        }
        return NoContent();
    }

    /// <summary>Called by the Claude Code notification hook when the agent is waiting or asks a question.</summary>
    [HttpPost("notify")]
    public async Task<IActionResult> Notify(string id, [FromBody] NotifyBody body, CancellationToken ct)
    {
        var rec = await AuthAsync(id, ct);
        if (rec is null) return Unauthorized();

        await _store.SetQuestionPendingAsync(id, true, ct);
        await NotifyAllAsync(rec, body.Event ?? "question",
            string.IsNullOrWhiteSpace(body.Message) ? "The agent is waiting for your reply." : body.Message, ct);
        return NoContent();
    }

    public record ResourcesBody(double CpuSeconds, long MemoryBytes, long RxBytes, long TxBytes);

    /// <summary>
    /// Periodic pod resource snapshot (cgroup CPU/memory, /proc/net/dev counters), posted by
    /// the session agent alongside its persistence heartbeat. Feeds the Usage dashboard.
    /// </summary>
    [HttpPost("resources")]
    public async Task<IActionResult> Resources(string id, [FromBody] ResourcesBody body, CancellationToken ct)
    {
        var rec = await AuthAsync(id, ct);
        if (rec is null) return Unauthorized();
        if (_usage is null) return NoContent();

        var sample = new SessionResourceSample(body.CpuSeconds, body.MemoryBytes, body.RxBytes, body.TxBytes);
        if (!sample.IsValid) return BadRequest();
        await _usage.AddResourceSampleAsync(id, rec.Owner, sample, ct);
        return NoContent();
    }

    /// <summary>
    /// The pod reports that its provider account hit a usage limit — Claude's mod read the
    /// rate-limit windows, or the session agent matched the CLI's own notice. The hub marks the
    /// account and, where it can, moves the session to another one (docs/account-limits.md).
    /// The answer says what happened so the pod can log it; nothing in it is acted on there.
    /// </summary>
    [HttpPost("account-exhausted")]
    public async Task<IActionResult> AccountExhausted(string id, [FromBody] AccountExhaustedReport body, CancellationToken ct)
    {
        var rec = await AuthAsync(id, ct);
        if (rec is null) return Unauthorized();
        if (body.Source is not ("mod" or "output")) return BadRequest("source must be mod or output");
        if (_accountFailover is null) return Ok(new AccountFailoverOutcome(null, null, AccountFailoverOutcome.Ignored));
        return Ok(await _accountFailover.HandleAsync(rec, body, ct));
    }

    /// <summary>Persists a subscription credential file uploaded by the matching provider agent.</summary>
    [HttpPut("{agent}-credentials")]
    public async Task<IActionResult> ProviderCredentials(string id, string agent, CancellationToken ct)
    {
        if (!Enum.TryParse<AgentKind>(agent, ignoreCase: true, out var parsedAgent) ||
            parsedAgent is not AgentKind.Claude and not AgentKind.Codex and not AgentKind.Cursor
                and not AgentKind.OpenClaw)
            return BadRequest();

        var rec = await AuthAsync(id, ct);
        if (rec is null) return Unauthorized();
        if (rec.Agent != parsedAgent || rec.AuthMode != AgentAuthMode.Subscription) return Conflict();

        var json = await ReadProviderCredentialBodyAsync(ct);
        if (json is null || !ProviderCredentialValidator.Validate(parsedAgent, json)) return BadRequest();

        // The Claude watcher sends who the login belongs to in a header, since its file does not
        // say; the other providers' files carry it themselves. Display only, never authorised on.
        var identity = ProviderAccountIdentityReader.FromHeader(Request.Headers[ProviderAccountIdentityReader.HeaderName])
            ?? ProviderAccountIdentityReader.FromFile(parsedAgent, json);
        var accountId = await _svc.StoreProviderLoginAsync(rec.Owner, parsedAgent, json, identity, rec.CredentialId, ct);
        // A login that turned out to be a different account than the one mounted, or the first
        // login of a session that had none, re-points the session so later rotations land there.
        if (accountId is not null && accountId != rec.CredentialId)
            await _store.SetCredentialIdAsync(rec.Id, accountId, ct);
        return NoContent();
    }

    /// <summary>
    /// Receives the terminal scrollback and stores it in Postgres so transcripts
    /// are available even without S3 (uploaded periodically and on exit).
    /// </summary>
    [HttpPut("scrollback")]
    public async Task<IActionResult> Scrollback(string id, CancellationToken ct)
    {
        var rec = await AuthAsync(id, ct);
        if (rec is null) return Unauthorized();

        using var reader = new StreamReader(Request.Body);
        var text = await reader.ReadToEndAsync(ct);
        if (text.Length > ScrollbackLimits.MaxChars) text = text[^ScrollbackLimits.MaxChars..];
        await _store.SetScrollbackAsync(id, text, ct);
        return NoContent();
    }

    /// <summary>
    /// Receives the provider's own transcript file (JSONL) and keeps its capped tail in
    /// Postgres; the whole file goes to S3 through a presigned URL. The role-based conversation
    /// the web app, the remote API and the MCP tools show is read from this, not from the
    /// scrollback — see docs/transcripts.md.
    /// </summary>
    [HttpPut("transcript")]
    public async Task<IActionResult> Transcript(string id, CancellationToken ct)
    {
        var rec = await AuthAsync(id, ct);
        if (rec is null) return Unauthorized();

        using var reader = new StreamReader(Request.Body);
        var text = await reader.ReadToEndAsync(ct);
        await _store.SetTranscriptAsync(id, NativeTranscript.TrimToLineCap(text, ScrollbackLimits.MaxChars), ct);
        return NoContent();
    }

    /// <summary>
    /// Hands the stored scrollback back to a restarting agent. A resumed session runs in a
    /// fresh pod with an empty buffer, so without this everything said before the resume is
    /// missing from the replay every client gets on connect. Raw on purpose: the agent replays
    /// these bytes into a terminal and persists them again as its own scrollback, so anything
    /// stripped here is stripped from the history for good.
    /// </summary>
    [HttpGet("scrollback")]
    public async Task<IActionResult> GetScrollback(string id, CancellationToken ct)
    {
        var rec = await AuthAsync(id, ct);
        if (rec is null) return Unauthorized();
        return Content(await _svc.GetScrollbackAsync(rec.Owner, id, ct) ?? "", "text/plain");
    }

    public record PermissionBody(string Tool, string? Input);
    public record AgentPolicyBody(string Tool, JsonElement Input);

    /// <summary>
    /// The agent's PreToolUse hook asks whether a tool may run. An earlier
    /// "allow (don't ask again)" answers immediately; otherwise the request is
    /// created and relayed to the messengers (best effort) — it stays pending
    /// either way so the web app can answer it, and the hook polls by id.
    /// </summary>
    [HttpPost("permission")]
    public async Task<IActionResult> RequestPermission(string id, [FromBody] PermissionBody body, CancellationToken ct)
    {
        var rec = await AuthAsync(id, ct);
        if (rec is null) return Unauthorized();

        var tool = string.IsNullOrWhiteSpace(body.Tool) ? "a tool" : body.Tool.Trim();
        // Auto-approve is read off the session record on every request, so switching it on
        // (or off) applies to the running session without a restart. No request row is
        // created — there is nothing for anyone to decide.
        if (rec.AutoApprove) return Ok(new { decision = "allow" });
        if (await _permissions.IsAlwaysAllowedAsync(id, tool, ct))
            return Ok(new { decision = "allow" });

        var req = new PermissionRequest
        {
            Id = Guid.NewGuid().ToString("n")[..12],
            SessionId = id, Owner = rec.Owner,
            Tool = tool,
            Summary = PermissionRequestDescriptor.ForTool(body.Tool)
        };
        await _permissions.CreateAsync(req, ct);
        await PermissionRelay.TryPostAsync(_permNotifiers, req, ct);
        return Ok(new { id = req.Id });
    }

    /// <summary>Polled by the hook: returns "allow" | "allowAlways" | "deny" | "expired" | "pending".</summary>
    [HttpGet("permission/{reqId}")]
    public async Task<IActionResult> PermissionStatus(string id, string reqId, CancellationToken ct)
    {
        if (await AuthAsync(id, ct) is null) return Unauthorized();
        return Ok(new { decision = await _permissions.GetDecisionAsync(reqId, id, ct) ?? "pending" });
    }

    /// <summary>
    /// The hook gave up waiting: mark the request expired and defuse the chat prompt.
    /// Returns the final decision — if a click won the race against this expire, the
    /// hook gets that decision back and can still honor it.
    /// </summary>
    [HttpPost("permission/{reqId}/expire")]
    public async Task<IActionResult> ExpirePermission(string id, string reqId, CancellationToken ct)
    {
        if (await AuthAsync(id, ct) is null) return Unauthorized();
        var resolved = await _permissions.ResolveAsync(reqId, "expired", id, ct);
        if (resolved is null)
        {
            var existing = await _permissions.GetAsync(reqId, id, ct);
            return Ok(new { decision = existing?.Decision ?? "expired" });
        }
        if (resolved.Platform is { } platform)
            foreach (var e in _promptEditors.Where(e => e.Platform == platform))
                await e.MarkExpiredAsync(resolved, ct);
        return Ok(new { decision = "expired" });
    }

    /// <summary>Evaluates the live MCP restriction policy for this session.</summary>
    [HttpPost("mcp-policy")]
    public async Task<IActionResult> McpPolicy(string id, [FromBody] PermissionBody body, CancellationToken ct)
    {
        if (await AuthAsync(id, ct) is null) return Unauthorized();
        var policy = await _shares.GetMcpPolicyAsync(id, ct);
        var blocked = policy is not null && McpPolicyMatcher.IsBlocked(
            body.Tool ?? string.Empty, policy.BlockedServers, policy.BlockedTools);
        return Ok(new { decision = blocked ? "deny" : "allow" });
    }

    /// <summary>Evaluates persisted Codex policy after the authoritative live sharing policy.</summary>
    [HttpPost("agent-policy")]
    public async Task<IActionResult> AgentPolicy(string id, [FromBody] AgentPolicyBody body, CancellationToken ct)
    {
        var rec = await AuthAsync(id, ct);
        if (rec is null) return Unauthorized();

        if ((body.Tool ?? string.Empty).StartsWith("mcp__", StringComparison.Ordinal))
        {
            var sharing = await _shares.GetMcpPolicyAsync(id, ct);
            if (sharing is not null && McpPolicyMatcher.IsBlocked(
                    body.Tool ?? string.Empty, sharing.BlockedServers, sharing.BlockedTools))
                return Ok(new { decision = "deny", reason = "Blocked by the session MCP sharing policy." });
        }

        if (rec.Mode == SessionMode.Interactive)
            return Ok(new { decision = "ask", reason = "Interactive approval required." });

        AgentPolicy policy;
        try
        {
            policy = string.IsNullOrWhiteSpace(rec.AgentPolicyJson)
                ? new AgentPolicy()
                : JsonSerializer.Deserialize<AgentPolicy>(rec.AgentPolicyJson,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new AgentPolicy();
        }
        catch (JsonException)
        {
            policy = new AgentPolicy();
        }

        // Read off the record on every request, like the permission endpoint does, so toggling
        // auto-approve reaches a running session without a restart.
        var result = AgentPolicyMatcher.Decide(policy, body.Tool ?? string.Empty, body.Input,
            rec.AutoApprove);
        return Ok(new { decision = result.Decision, reason = result.Reason });
    }

    /// <summary>
    /// Skills accessible to the session owner (own + shared while licensed),
    /// fetched by the entrypoint and materialized under ~/.claude/skills.
    /// </summary>
    [HttpGet("skills")]
    public async Task<IActionResult> Skills(string id, CancellationToken ct)
    {
        var rec = await AuthAsync(id, ct);
        if (rec is null) return Unauthorized();
        var skills = await _library.ListSkillPayloadsAsync(rec.Owner, rec.ProjectId, ct);
        return Ok(new { skills });
    }

    /// <summary>Mints a presigned PUT URL so the agent can upload an artifact to S3.</summary>
    [HttpPost("artifact-url")]
    public async Task<IActionResult> ArtifactUrl(string id, [FromQuery] string name, CancellationToken ct)
    {
        if (!Request.Headers.TryGetValue("X-Agent-Token", out var tok)) return Unauthorized();
        var url = await _svc.MintArtifactUploadUrlAsync(id, tok!, name, ct);
        return url is null ? Unauthorized() : Ok(new { url });
    }

    /// <summary>
    /// Spawns a child session owned by the parent session's owner. Always sets
    /// <see cref="CreateSessionRequest.ParentSessionId"/> to this session id
    /// (client overrides that escape the parent are ignored).
    /// </summary>
    [HttpPost("spawn")]
    public async Task<IActionResult> Spawn(string id, [FromBody] CreateSessionRequest req, CancellationToken ct)
    {
        if (!_spawnMcpEnabled) return NotFound();

        var rec = await AuthAsync(id, ct);
        if (rec is null) return Unauthorized();

        // A child joins its parent's project and MCP servers unless the request names its own. The
        // agent does not know its project id, so without this every spawned session landed outside
        // the fleet: absent from agents_list, unreachable for agent_send, and without the MCP tools
        // the parent works with.
        var inheritMcp = string.IsNullOrWhiteSpace(req.McpConfigJson) && req.McpServerIds is null or { Count: 0 };
        var forced = req with
        {
            ParentSessionId = id,
            ProjectId = req.ProjectId ?? rec.ProjectId,
            McpConfigJson = inheritMcp ? rec.McpConfigJson : req.McpConfigJson,
            McpServerIds = inheritMcp ? await AccessibleParentServerIdsAsync(rec, ct) : req.McpServerIds
        };
        try
        {
            return Ok(await _svc.CreateSessionAsync(rec.Owner, forced, ct));
        }
        catch (ArgumentException e)
        {
            return BadRequest(e.Message);
        }
        catch (SessionLimitExceededException e)
        {
            return StatusCode(StatusCodes.Status429TooManyRequests, e.Message);
        }
    }

    /// <summary>Lists direct children of this session (same owner, ParentSessionId == id).</summary>
    [HttpGet("children")]
    public async Task<ActionResult<IReadOnlyList<SessionInfo>>> Children(string id, CancellationToken ct)
    {
        var rec = await AuthAsync(id, ct);
        if (rec is null) return Unauthorized();

        var all = await _svc.ListSessionsAsync(rec.Owner, ct);
        return Ok(all.Where(s => s.ParentSessionId == id).ToList());
    }

    /// <summary>Gets a peer session if it is a descendant of this session (same owner).</summary>
    [HttpGet("peer/{childId}")]
    public async Task<IActionResult> GetPeer(string id, string childId, CancellationToken ct)
    {
        var rec = await AuthAsync(id, ct);
        if (rec is null) return Unauthorized();
        if (!await IsDescendantPeerAsync(rec, childId, ct)) return NotFound();

        var info = await _svc.GetSessionAsync(rec.Owner, childId, ct);
        return info is null ? NotFound() : Ok(info);
    }

    /// <summary>Deletes a peer session if it is a descendant of this session (same owner).</summary>
    [HttpDelete("peer/{childId}")]
    public async Task<IActionResult> DeletePeer(string id, string childId, CancellationToken ct)
    {
        var rec = await AuthAsync(id, ct);
        if (rec is null) return Unauthorized();
        if (!await IsDescendantPeerAsync(rec, childId, ct)) return NotFound();

        try
        {
            await _svc.DeleteSessionAsync(rec.Owner, childId, ct);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    /// <summary>
    /// Converts a descendant's finished autonomous run into an interactive session — an
    /// orchestrator handing one of its children to a person. Same descendant rule as GetPeer, so
    /// nothing about a session outside the caller's line leaks through the status code.
    /// </summary>
    [HttpPost("peer/{childId}/convert")]
    public async Task<IActionResult> ConvertPeer(string id, string childId, [FromBody] ConvertSessionRequest req,
        CancellationToken ct)
    {
        var rec = await AuthAsync(id, ct);
        if (rec is null) return Unauthorized();
        if (!await IsDescendantPeerAsync(rec, childId, ct)) return NotFound();

        try { return Ok(await _svc.ConvertSessionAsync(rec.Owner, childId, req, ct)); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (ArgumentException e) { return BadRequest(new { error = e.Message }); }
        catch (InvalidOperationException e) { return Conflict(new { error = e.Message }); }
    }

    /// <summary>
    /// The account this session runs on and the alternatives (docs/account-limits.md), for the
    /// in-pod <c>account_status</c> tool; <c>peer/{childId}/account-status</c> answers the same
    /// for a descendant.
    /// </summary>
    [HttpGet("account-status")]
    public Task<IActionResult> AccountStatus(string id, CancellationToken ct) => AccountStatusOfAsync(id, null, ct);

    [HttpGet("peer/{childId}/account-status")]
    public Task<IActionResult> PeerAccountStatus(string id, string childId, CancellationToken ct) => AccountStatusOfAsync(id, childId, ct);

    private async Task<IActionResult> AccountStatusOfAsync(string id, string? childId, CancellationToken ct)
    {
        var rec = await AuthAsync(id, ct);
        if (rec is null) return Unauthorized();
        if (childId is not null && !await IsDescendantPeerAsync(rec, childId, ct)) return NotFound();
        var session = await _svc.GetSessionAsync(rec.Owner, childId ?? id, ct);
        if (session is null) return NotFound();
        return Ok(Models.AccountStatus.From(session, await _svc.ListProviderAccountsAsync(rec.Owner, ct)));
    }

    /// <summary>
    /// Moves this session — or a descendant — to another of the owner's accounts, the same
    /// switch the in-app header offers. A session switching itself is allowed on purpose: an
    /// agent that reads "usage limit" in its own output can save itself (docs/account-limits.md).
    /// </summary>
    [HttpPatch("credential")]
    public Task<IActionResult> SwitchCredential(string id, [FromBody] SwitchSessionCredentialRequest body, CancellationToken ct)
        => SwitchCredentialOfAsync(id, null, body, ct);

    [HttpPatch("peer/{childId}/credential")]
    public Task<IActionResult> SwitchPeerCredential(string id, string childId, [FromBody] SwitchSessionCredentialRequest body,
        CancellationToken ct) => SwitchCredentialOfAsync(id, childId, body, ct);

    private async Task<IActionResult> SwitchCredentialOfAsync(string id, string? childId, SwitchSessionCredentialRequest body,
        CancellationToken ct)
    {
        var rec = await AuthAsync(id, ct);
        if (rec is null) return Unauthorized();
        if (childId is not null && !await IsDescendantPeerAsync(rec, childId, ct)) return NotFound();
        try { return Ok(await _svc.SwitchSessionCredentialAsync(rec.Owner, childId ?? id, body.CredentialId, ct)); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (ArgumentException e) { return BadRequest(new { error = e.Message }); }
        catch (InvalidOperationException e) { return Conflict(new { error = e.Message }); }
        catch (HttpRequestException e) { return StatusCode(StatusCodes.Status502BadGateway, new { error = e.Message }); }
    }

    private async Task<bool> IsDescendantPeerAsync(SessionRecord parent, string childId, CancellationToken ct)
    {
        var all = await _svc.ListSessionsAsync(parent.Owner, ct);
        var byId = all.ToDictionary(s => s.Id, s => s.ParentSessionId);
        // Ensure the authenticated parent itself is present for chain walks.
        byId.TryAdd(parent.Id, parent.ParentSessionId);
        return SessionDescent.IsDescendant(childId, parent.Id, id => byId.GetValueOrDefault(id));
    }

    // ------------------------------------------------------------- project agent fleet

    /// <summary>
    /// Directory of the session's project fleet: every session of the same owner in the
    /// same project, as slim <see cref="ProjectAgentInfo"/> records (no MCP configs, no
    /// runtime settings), plus the session's own ancestors and descendants whatever their project.
    /// </summary>
    [HttpGet("project-agents")]
    public async Task<IActionResult> ProjectAgents(string id, CancellationToken ct)
    {
        var rec = await AuthAsync(id, ct);
        if (rec is null) return Unauthorized();

        var all = await _svc.ListSessionsAsync(rec.Owner, ct);
        var parentOf = ParentLookup(rec, all);
        var scope = all.Where(s => s.Id == rec.Id || IsFleetPeer(rec, s.Id, s.ProjectId, parentOf));
        return Ok(scope
            .Select(s => new ProjectAgentInfo(s.Id, s.Title, s.Description, s.Phase, s.Mode,
                s.Agent, s.QuestionPending, s.CreatedAt, Self: s.Id == rec.Id))
            .ToList());
    }

    // Peers are the sessions of the same project plus the session's own line: its ancestors and
    // descendants. The line is what session_create builds, and a child started outside any
    // project — or before children inherited the project — had no way back to its parent.
    private static bool IsFleetPeer(SessionRecord self, string otherId, string? otherProjectId,
        Func<string, string?> parentOf) =>
        otherId != self.Id &&
        ((self.ProjectId is not null && otherProjectId == self.ProjectId) ||
         SessionDescent.IsDescendant(otherId, self.Id, parentOf) ||
         SessionDescent.IsDescendant(self.Id, otherId, parentOf));

    private static Func<string, string?> ParentLookup(SessionRecord self, IEnumerable<SessionInfo> all)
    {
        var byId = all.ToDictionary(s => s.Id, s => s.ParentSessionId);
        byId.TryAdd(self.Id, self.ParentSessionId);
        return id => byId.GetValueOrDefault(id);
    }

    // Create resolves library servers strictly, so a server deleted or unshared since the parent
    // started would fail every spawn that inherits it. The child gets what the parent still has.
    private async Task<List<string>> AccessibleParentServerIdsAsync(SessionRecord parent, CancellationToken ct)
    {
        var ids = ParseServerIds(parent.McpServerIdsJson);
        if (ids.Count == 0) return ids;
        var accessible = (await _library.ResolveMcpServersAsync(parent.Owner, ids, strict: false, ct))
            .Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        return ids.Where(accessible.Contains).ToList();
    }

    private static List<string> ParseServerIds(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? new(); }
        catch (JsonException) { return new(); }
    }

    /// <summary>
    /// Sends a message/task from this session to a peer agent: same owner, and either the same
    /// (non-null) project or the session's own ancestor/descendant line — anything else answers
    /// 404 so nothing about foreign sessions leaks.
    /// </summary>
    [HttpPost("messages")]
    public async Task<IActionResult> SendMessage(string id, [FromBody] SendAgentMessageRequest body, CancellationToken ct)
    {
        var rec = await AuthAsync(id, ct);
        if (rec is null) return Unauthorized();
        if (_messages is null) return NotFound();

        var text = AgentMessaging.NormalizeBody(body.Body);
        if (text is null)
            return BadRequest($"A message body of 1..{AgentMessaging.MaxBodyChars} characters is required.");
        var to = body.To?.Trim();
        if (string.IsNullOrEmpty(to)) return BadRequest("A target session id is required.");
        if (to == id) return BadRequest("A session cannot message itself.");
        var target = await _store.GetAsync(rec.Owner, to, ct);
        if (target is null) return NotFound();
        var all = await _svc.ListSessionsAsync(rec.Owner, ct);
        var parentOf = ParentLookup(rec, all);
        if (!IsFleetPeer(rec, target.Id, target.ProjectId, parentOf)) return NotFound();

        var (priority, interrupt) = AgentMessaging.ResolveFlags(body.Priority, body.Interrupt);
        var message = new SessionMessageRecord
        {
            Id = Guid.NewGuid().ToString("n")[..12],
            ProjectId = rec.ProjectId,
            FromSessionId = rec.Id,
            ToSessionId = target.Id,
            Owner = rec.Owner,
            Body = text,
            Priority = priority,
            Interrupt = interrupt
        };
        await _messages.AddAsync(message, ct);
        // Best-effort visibility beyond the pull inbox: the same fan-out that carries
        // question/finished events to Slack/Telegram/n8n. The web app reads the message
        // itself from the public messages endpoint.
        await NotifyAllAsync(target, "agent-message",
            $"Agent \"{rec.Title}\" sent a message to \"{target.Title}\": {Truncate(text, 300)}", ct);
        var delivery = await AgentMessageDispatch.PushAsync(_delivery,
            all.FirstOrDefault(s => s.Id == target.Id), message, rec.Title, ct);
        return Ok(new AgentMessageSendResult(message.Id, target.Id, delivery.Via, delivery.Reason));
    }

    /// <summary>
    /// The session's inbox: takes undelivered messages (marking them delivered), long-polling
    /// up to <paramref name="wait"/> seconds. Batches are capped so a full response stays
    /// under the in-pod MCP client's response limit — callers poll again for the rest.
    /// </summary>
    [HttpGet("messages")]
    public async Task<IActionResult> InboxMessages(string id, CancellationToken ct, [FromQuery] int wait = 0)
    {
        var rec = await AuthAsync(id, ct);
        if (rec is null) return Unauthorized();
        if (_messages is null) return Ok(new { messages = Array.Empty<AgentMessageInfo>() });

        var deadline = DateTime.UtcNow.AddSeconds(Math.Clamp(wait, 0, AgentMessaging.MaxWaitSeconds));
        IReadOnlyList<SessionMessageRecord> taken;
        while (true)
        {
            taken = await _messages.TakeUndeliveredAsync(id, AgentMessaging.InboxBatchLimit, ct);
            if (taken.Count > 0 || DateTime.UtcNow >= deadline) break;
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }

        var titles = taken.Any(m => m.FromSessionId is not null)
            ? (await _svc.ListSessionsAsync(rec.Owner, ct)).ToDictionary(s => s.Id, s => s.Title)
            : new Dictionary<string, string>();
        var messages = taken.Select(m => new AgentMessageInfo(
            m.Id, m.FromSessionId,
            m.FromSessionId is null ? null : titles.GetValueOrDefault(m.FromSessionId),
            m.Body, m.CreatedAt, m.DeliveredAt, m.Priority, m.Interrupt, m.DeliveredVia)).ToList();
        return Ok(new { messages });
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";
}
