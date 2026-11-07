using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using AgentHub.Api.Auth;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

static int Port()
{
    var socket = new TcpListener(IPAddress.Loopback, 0);
    socket.Start(); var port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop(); return port;
}
static WebApplication Host(int port)
{
    var builder = WebApplication.CreateBuilder();
    builder.Logging.ClearProviders();
    builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
    return builder.Build();
}
using var rsa = RSA.Create(2048);
var key = new RsaSecurityKey(rsa) { KeyId = "proof-key" };
var parameters = rsa.ExportParameters(false);
var issuer = $"http://127.0.0.1:{Port()}";
await using var identity = Host(new Uri(issuer).Port);
identity.MapGet("/.well-known/openid-configuration", () => Results.Json(new {
    issuer, jwks_uri = issuer + "/keys", authorization_endpoint = issuer + "/authorize",
    token_endpoint = issuer + "/token", response_types_supported = new[] { "code" },
    subject_types_supported = new[] { "public" }, id_token_signing_alg_values_supported = new[] { "RS256" }
}));
identity.MapGet("/keys", () => Results.Json(new { keys = new[] { new {
    kty = "RSA", use = "sig", kid = key.KeyId, alg = "RS256",
    n = Base64UrlEncoder.Encode(parameters.Modulus!), e = Base64UrlEncoder.Encode(parameters.Exponent!)
} } }));
await identity.StartAsync();

async Task Scenario(string authority, bool https, bool validate, bool expectStartupFailure, HttpStatusCode expected)
{
    var builder = WebApplication.CreateBuilder();
    builder.Logging.ClearProviders();
    var port = Port(); builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o => {
        o.Authority = authority; o.Audience = "agenthub"; o.RequireHttpsMetadata = https;
        o.BackchannelTimeout = TimeSpan.FromSeconds(2);
    });
    builder.Services.AddAuthorization();
    await using var app = builder.Build();
    app.UseAuthentication(); app.UseAuthorization();
    app.MapGet("/api/config", () => Results.Ok(new { ready = true }));
    app.MapGet("/private", (ClaimsPrincipal user) => user.FindFirst("preferred_username")!.Value).RequireAuthorization();
    var failed = false;
    try { if (validate) await OidcStartupValidation.ValidateAsync(app.Services); }
    catch (HubAuthConfigurationException e) {
        failed = true;
        if (!e.Message.Contains("oidc.requireHttpsMetadata=false")) throw;
    }
    if (failed != expectStartupFailure) throw new Exception("Wrong startup result");
    if (!failed)
    {
        await app.StartAsync();
        using var client = new HttpClient();
        var response = await client.GetAsync($"http://127.0.0.1:{port}/api/config");
        if (response.StatusCode != expected) throw new Exception($"Expected {expected}, got {response.StatusCode}");
        if (validate) {
            var token = new JwtSecurityToken(issuer, "agenthub", [new Claim("preferred_username", "team-admin")],
                DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(5), new SigningCredentials(key, SecurityAlgorithms.RsaSha256));
            client.DefaultRequestHeaders.Authorization = new("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
            var authenticated = await client.GetStringAsync($"http://127.0.0.1:{port}/private");
            if (authenticated != "team-admin") throw new Exception("JWT owner mismatch");
        }
        await app.StopAsync();
    }
    Console.WriteLine($"PASS https={https}, startupValidation={validate}, stopped={failed}, expectedHTTP={expected}");
}
await Scenario(issuer, true, false, false, HttpStatusCode.InternalServerError);
await Scenario(issuer, true, true, true, HttpStatusCode.OK);
await Scenario($"http://127.0.0.1:{Port()}", false, true, true, HttpStatusCode.OK);
await Scenario(issuer, false, true, false, HttpStatusCode.OK);
await identity.StopAsync();
