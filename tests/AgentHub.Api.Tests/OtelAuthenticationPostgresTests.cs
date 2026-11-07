using AgentHub.Api.Controllers;
using AgentHub.Api.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentHub.Api.Tests;

public class OtelAuthenticationPostgresTests
{
    [PostgreSqlFact]
    public async Task Telemetry_RequiresCredentialAndMatchingSessionBeforeAnyWrite()
    {
        await using var db = await PostgresUsageDatabase.CreateAsync();
        await db.AddSessionAsync("own", "alice", AgentAuthMode.ApiKey);
        await db.AddSessionAsync("other", "bob", AgentAuthMode.ApiKey);
        var own = new OtlpMetricsParserTests.OtlpBuilder()
            .Resource(("agenthub.session_id", "own"), ("agenthub.owner", "bob"))
            .TokenPoint("input", 10).Build();
        var other = new OtlpMetricsParserTests.OtlpBuilder()
            .Resource(("session.id", "other"))
            .TokenPoint("input", 20).Build();

        async Task<IActionResult> Send(byte[] body, string? token)
        {
            var context = new DefaultHttpContext();
            context.Request.Body = new MemoryStream(body);
            if (token is not null) context.Request.Headers["X-Agent-Token"] = token;
            var controller = new OtelController(db.Usage, NullLogger<OtelController>.Instance, db.Sessions)
            {
                ControllerContext = new ControllerContext { HttpContext = context }
            };
            return await controller.Metrics(CancellationToken.None);
        }

        Assert.IsType<UnauthorizedResult>(await Send(own, null));
        Assert.IsType<UnauthorizedResult>(await Send(own, "unknown"));
        Assert.IsType<BadRequestResult>(await Send(other, "token-own"));
        Assert.IsType<BadRequestResult>(await Send(own.Concat(other).ToArray(), "token-own"));
        Assert.Empty(await db.Usage.ListByOwnerAsync("alice"));
        Assert.Empty(await db.Usage.ListByOwnerAsync("bob"));

        var accepted = await Send(own, "token-own");
        var response = new DefaultHttpContext();
        await accepted.ExecuteResultAsync(new ActionContext { HttpContext = response });
        Assert.Equal(200, response.Response.StatusCode);
        Assert.Equal(10, Assert.Single(await db.Usage.ListByOwnerAsync("alice")).InputTokens);
        Assert.Empty(await db.Usage.ListByOwnerAsync("bob"));
    }
}
