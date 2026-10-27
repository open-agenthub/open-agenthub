using System.Text.Encodings.Web;
using AgentHub.Api.Admin;
using AgentHub.Api.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentHub.Api.Tests;

public class HubAuthTests
{
    private const string Key = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKL";

    private static IConfiguration Config(params (string Key, string Value)[] values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    [Fact]
    public void Nothing_configured_refuses_to_start()
    {
        // The chart default used to be "no OIDC = every request is admin user dev"; an install
        // that forgot the login must not come up at all.
        var e = Assert.Throws<HubAuthConfigurationException>(() => HubAuthOptions.Resolve(Config()));
        Assert.Contains("Oidc:Authority", e.Message);
        Assert.Contains("Auth:ApiKey", e.Message);
        Assert.Contains("Auth:InsecureDevMode", e.Message);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("yes")]
    [InlineData("")]
    public void Only_an_explicit_true_opts_into_insecure_mode(string value)
        => Assert.Throws<HubAuthConfigurationException>(() =>
            HubAuthOptions.Resolve(Config(("Auth:InsecureDevMode", value))));

    [Fact]
    public void Insecure_mode_needs_the_explicit_opt_in()
    {
        var auth = HubAuthOptions.Resolve(Config(("Auth:InsecureDevMode", "true")));
        Assert.Equal(HubAuthMode.InsecureDev, auth.Mode);
        Assert.Equal("insecureDev", auth.ConfigName);
        Assert.False(auth.ApiKeyMatches("anything"));
    }

    [Fact]
    public void Oidc_wins_over_a_key_and_the_insecure_flag()
    {
        var auth = HubAuthOptions.Resolve(Config(
            ("Oidc:Authority", "https://sso.example.com/realms/agenthub"),
            ("Auth:ApiKey", Key), ("Auth:InsecureDevMode", "true")));
        Assert.Equal(HubAuthMode.Oidc, auth.Mode);
        // A key that is configured but not in force must not open a second door.
        Assert.False(auth.ApiKeyMatches(Key));
    }

    [Fact]
    public void A_key_wins_over_the_insecure_flag_and_defaults_to_owner_admin()
    {
        var auth = HubAuthOptions.Resolve(Config(("Auth:ApiKey", Key), ("Auth:InsecureDevMode", "true")));
        Assert.Equal(HubAuthMode.ApiKey, auth.Mode);
        Assert.Equal("apiKey", auth.ConfigName);
        Assert.Equal("admin", auth.ApiKeyOwner);
        Assert.Equal("dev", HubAuthOptions.Resolve(Config(("Auth:ApiKey", Key), ("Auth:ApiKeyOwner", "dev"))).ApiKeyOwner);
    }

    [Theory]
    [InlineData("changeme")]
    [InlineData("0123456789abcdef0123456789abcde")] // 31
    public void A_short_key_refuses_to_start(string key)
    {
        // A placeholder left in a values file would look secured without being so.
        var e = Assert.Throws<HubAuthConfigurationException>(() => HubAuthOptions.Resolve(Config(("Auth:ApiKey", key))));
        Assert.DoesNotContain(key, e.Message);
    }

    [Theory]
    [InlineData(Key, true)]
    [InlineData(" " + Key + "\n", true)] // a Secret written with `echo` carries a newline
    [InlineData(Key + "x", false)]
    [InlineData("0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJK", false)] // prefix
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Matching_is_exact(string? presented, bool matches)
        => Assert.Equal(matches, HubAuthOptions.ForApiKey(Key).ApiKeyMatches(presented));

    private sealed class Monitor : IOptionsMonitor<AuthenticationSchemeOptions>
    {
        public AuthenticationSchemeOptions CurrentValue { get; } = new();
        public AuthenticationSchemeOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<AuthenticationSchemeOptions, string?> listener) => null;
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static async Task<AuthenticateResult> AuthenticateAsync(Action<HttpRequest> request, string owner = "admin")
        => (await RequestAsync(request, new ApiKeyFailureLimiter(), challenge: false, owner)).Result;

    // One request through the handler as the authentication middleware and an [Authorize]
    // endpoint drive it: authenticate, and challenge when that did not succeed.
    private static async Task<(AuthenticateResult Result, int Status)> RequestAsync(
        Action<HttpRequest> request, ApiKeyFailureLimiter limiter, bool challenge = true, string owner = "admin")
    {
        var handler = new ApiKeyAuthHandler(new Monitor(), NullLoggerFactory.Instance, UrlEncoder.Default,
            HubAuthOptions.ForApiKey(Key, owner), limiter);
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("10.0.0.7");
        request(ctx.Request);
        await handler.InitializeAsync(
            new AuthenticationScheme(ApiKeyAuthHandler.SchemeName, null, typeof(ApiKeyAuthHandler)), ctx);
        var result = await handler.AuthenticateAsync();
        if (challenge && !result.Succeeded) await handler.ChallengeAsync(null);
        return (result, ctx.Response.StatusCode);
    }

    private static Action<HttpRequest> WithKey(string key, string? forwardedFor = null) => r =>
    {
        r.Headers.Authorization = $"Bearer {key}";
        if (forwardedFor is not null) r.Headers["X-Forwarded-For"] = forwardedFor;
    };

    [Fact]
    public async Task Wrong_keys_from_one_client_are_throttled_and_the_right_key_with_them()
    {
        var clock = new Clock();
        var limiter = new ApiKeyFailureLimiter(clock);
        for (var i = 0; i < ApiKeyFailureLimiter.MaxFailuresPerClient; i++)
            Assert.Equal(401, (await RequestAsync(WithKey("wrong-" + i, "203.0.113.5"), limiter)).Status);

        // Letting a correct guess through would not limit guessing at all.
        var limited = await RequestAsync(WithKey(Key, "203.0.113.5"), limiter);
        Assert.Equal(429, limited.Status);
        Assert.False(limited.Result.Succeeded);

        // Behind the ingress every connection shares its address; another client is unaffected.
        Assert.True((await RequestAsync(WithKey(Key, "198.51.100.9"), limiter)).Result.Succeeded);

        clock.Now += ApiKeyFailureLimiter.Window;
        Assert.True((await RequestAsync(WithKey(Key, "203.0.113.5"), limiter)).Result.Succeeded);
    }

    [Fact]
    public async Task Rotating_an_invented_forwarded_header_does_not_escape_the_address_limit()
    {
        var limiter = new ApiKeyFailureLimiter(new Clock());
        for (var i = 0; i < ApiKeyFailureLimiter.MaxFailuresPerAddress; i++)
            await RequestAsync(WithKey("wrong", $"192.0.2.{i % 250}, 10.9.{i / 250}.{i % 250}"), limiter);
        Assert.Equal(429, (await RequestAsync(WithKey(Key, "192.0.2.251"), limiter)).Status);
    }

    [Fact]
    public async Task Only_a_challenge_counts_so_other_bearer_tokens_never_throttle()
    {
        // Personal tokens on /api/remote and OAuth tokens on /mcp also pass through this handler,
        // on endpoints that never challenge it. Fifty of them must not lock the owner out.
        var limiter = new ApiKeyFailureLimiter(new Clock());
        for (var i = 0; i < 50; i++)
            Assert.False((await RequestAsync(WithKey("oah_personal-token"), limiter, challenge: false)).Result.Succeeded);
        Assert.True((await RequestAsync(WithKey(Key), limiter)).Result.Succeeded);
    }

    [Fact]
    public async Task The_right_key_is_the_configured_owner()
    {
        var result = await AuthenticateAsync(r => r.Headers.Authorization = $"Bearer {Key}", owner: "ops");
        Assert.True(result.Succeeded);
        Assert.Equal("ops", result.Principal!.FindFirst("preferred_username")!.Value);
        Assert.Equal("ops", result.Principal.Identity!.Name);
    }

    [Fact]
    public async Task A_wrong_key_fails_without_echoing_it()
    {
        var result = await AuthenticateAsync(r => r.Headers.Authorization = "Bearer not-the-key-but-long-enough-0000000000");
        Assert.False(result.Succeeded);
        Assert.NotNull(result.Failure);
        Assert.DoesNotContain("not-the-key", result.Failure!.Message);
    }

    [Fact]
    public async Task No_credential_is_no_result_so_anonymous_endpoints_keep_their_own_tokens()
    {
        // Internal callbacks and the remote API with personal tokens are [AllowAnonymous] and read
        // their own headers; this scheme must not turn their requests into failures.
        Assert.True((await AuthenticateAsync(_ => { })).None);
        Assert.True((await AuthenticateAsync(r => r.Headers.Authorization = "Basic abc")).None);
    }

    [Fact]
    public async Task WebSockets_carry_the_key_in_access_token_only_on_ws_paths()
    {
        var ws = await AuthenticateAsync(r => { r.Path = "/ws/sessions/s1/terminal"; r.QueryString = new QueryString($"?access_token={Key}"); });
        Assert.True(ws.Succeeded);

        // Anywhere else a key in the URL would end up in access logs and browser history.
        var rest = await AuthenticateAsync(r => { r.Path = "/api/sessions"; r.QueryString = new QueryString($"?access_token={Key}"); });
        Assert.True(rest.None);

        var wrong = await AuthenticateAsync(r => { r.Path = "/ws/sessions/s1/terminal"; r.QueryString = new QueryString("?access_token=nope"); });
        Assert.False(wrong.Succeeded);
    }

    [Fact]
    public async Task The_key_holder_is_an_admin_even_when_Ee_Admins_names_someone_else()
    {
        var access = new AdminAccess(Config(("Ee:Admins", "alice")), NullLogger<AdminAccess>.Instance, [],
            hubAuth: HubAuthOptions.ForApiKey(Key, "admin"));
        Assert.True(await access.IsAdminAsync("admin"));
        Assert.True(await access.IsAdminAsync("alice"));
        Assert.False(await access.IsAdminAsync("mallory"));
        // The owner counts as a configured admin, so bootstrap mode — everyone is admin — is off.
        Assert.False(access.Bootstrap);
    }
}
