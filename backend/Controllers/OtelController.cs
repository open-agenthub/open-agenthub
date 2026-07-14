using AgentHub.Api.Otel;
using AgentHub.Api.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentHub.Api.Controllers;

/// <summary>
/// OTLP/HTTP metrics ingest. Called ONLY by the agent pods' Claude Code telemetry exporter
/// (protobuf over HTTP). No user auth — like <see cref="InternalController"/> it is reachable
/// only inside the cluster (not routed through the ingress). Session/owner attribution comes
/// from the payload's <c>session.id</c>/<c>user.id</c> attributes, cross-checked against the
/// sessions registry, so a stray payload cannot bill another user.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("internal/otel")]
public sealed class OtelController : ControllerBase
{
    private readonly IUsageStore _usage;
    private readonly ILogger<OtelController> _log;

    public OtelController(IUsageStore usage, ILogger<OtelController> log)
    {
        _usage = usage;
        _log = log;
    }

    /// <summary>
    /// Receives an OTLP <c>ExportMetricsServiceRequest</c>. The OTEL SDK appends this
    /// <c>/v1/metrics</c> path to the configured endpoint base.
    /// </summary>
    [HttpPost("v1/metrics")]
    public async Task<IActionResult> Metrics(CancellationToken ct)
    {
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
