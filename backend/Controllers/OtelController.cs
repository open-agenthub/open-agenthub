using AgentHub.Api.Otel;
using AgentHub.Api.Files;
using AgentHub.Api.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentHub.Api.Controllers;

/// <summary>
/// OTLP/HTTP metrics ingest authenticated with the session callback credential.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("internal/otel")]
public sealed class OtelController : ControllerBase
{
    private readonly IAgentCallbackAuthorizer? _executionAuthority;
    private readonly IUsageStore _usage;
    private readonly ISessionStore _sessions;
    private readonly ILogger<OtelController> _log;

    public OtelController(IUsageStore usage, ILogger<OtelController> log, ISessionStore sessions, IAgentCallbackAuthorizer? executionAuthority = null)
    {
        _executionAuthority = executionAuthority;
        _usage = usage;
        _sessions = sessions;
        _log = log;
    }

    /// <summary>
    /// Receives an OTLP <c>ExportMetricsServiceRequest</c>. The OTEL SDK appends this
    /// <c>/v1/metrics</c> path to the configured endpoint base.
    /// </summary>
    [HttpPost("v1/metrics")]
    public async Task<IActionResult> Metrics(CancellationToken ct)
    {
        if (!Request.Headers.TryGetValue("X-Agent-Token", out var token) || string.IsNullOrWhiteSpace(token))
            return Unauthorized();
        var session = await _sessions.GetByCallbackTokenAsync(token!, ct);
        if (session is null) return Unauthorized();
        if (_executionAuthority is not null)
        {
            session = await _executionAuthority.AuthorizeAsync(Request, session.Id, ct);
            if (session is null) return Unauthorized();
        }

        // Read the raw protobuf body (capped to protect against oversized payloads).
        using var ms = new MemoryStream();
        await Request.Body.CopyToAsync(ms, ct);
        if (ms.Length == 0 || ms.Length > 8_000_000) return BadRequest();

        IReadOnlyList<SessionUsageDelta> deltas;
        try
        {
            deltas = OtlpMetricsParser.Parse(ms.GetBuffer().AsSpan(0, (int)ms.Length));
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Failed to parse OTLP metrics payload ({Bytes} bytes)", ms.Length);
            return BadRequest();
        }

        // Validate the entire batch before writing so a mixed batch cannot partially count.
        if (deltas.Any(d => !string.Equals(d.SessionId, session.Id, StringComparison.Ordinal)))
            return BadRequest();

        var stored = 0;
        foreach (var d in deltas)
            if (await _usage.AddDeltaAsync(d, ct)) stored++;
        _log.LogInformation("OTLP metrics: {Bytes}B, {Deltas} session-delta(s), {Stored} stored; sessions=[{Ids}]",
            ms.Length, deltas.Count, stored, string.Join(",", deltas.Select(d => d.SessionId)));

        // OTLP expects an ExportMetricsServiceResponse; an empty protobuf message (zero bytes)
        // is a valid "everything accepted" response.
        return new EmptyProtobufResult();
    }

    private sealed class EmptyProtobufResult : IActionResult
    {
        public Task ExecuteResultAsync(ActionContext context)
        {
            var res = context.HttpContext.Response;
            res.StatusCode = StatusCodes.Status200OK;
            res.ContentType = "application/x-protobuf";
            res.ContentLength = 0;
            return Task.CompletedTask;
        }
    }
}
