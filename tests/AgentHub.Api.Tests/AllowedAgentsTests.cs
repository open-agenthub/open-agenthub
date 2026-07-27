using System.Security.Claims;
using AgentHub.Api.Admin;
using AgentHub.Api.Agents;
using AgentHub.Api.Ee.Agents;
using AgentHub.Api.Licensing;
using AgentHub.Api.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentHub.Api.Tests;

public class AllowedAgentsTests
{
    [Fact]
    public async Task CeProvider_AllowsAllKnownAgents()
    {
        var p = new AllowAllAgentsProvider();
        var allowed = await p.GetAllowedAsync(CancellationToken.None);
        Assert.Contains(AgentKind.OpenClaw, allowed);
        Assert.Equal(Enum.GetValues<AgentKind>().Length, allowed.Count);
        Assert.True(await p.IsAllowedAsync(AgentKind.Cursor));
        Assert.True(await p.IsAllowedAsync(AgentKind.Claude));
    }

    [Fact]
    public async Task EeProvider_WithoutConfig_AllowsAll_WhenLicensed()
    {
        var store = AllowedAgentsStore.InMemory();
        var p = new EeAllowedAgentsProvider(new FakeLicense(true), store);

        var allowed = await p.GetAllowedAsync();
        Assert.Equal(Enum.GetValues<AgentKind>().Length, allowed.Count);
        Assert.True(await p.IsAllowedAsync(AgentKind.OpenClaw));
    }

    [Fact]
    public async Task EeProvider_WithWhitelist_Restricts()
    {
        var store = AllowedAgentsStore.InMemory();
        await store.ReplaceAsync(new[] { AgentKind.Claude, AgentKind.OpenClaw });
        var p = new EeAllowedAgentsProvider(new FakeLicense(true), store);

        var allowed = await p.GetAllowedAsync();
        Assert.Equal(2, allowed.Count);
        Assert.Contains(AgentKind.Claude, allowed);
        Assert.Contains(AgentKind.OpenClaw, allowed);
        Assert.True(await p.IsAllowedAsync(AgentKind.Claude));
        Assert.False(await p.IsAllowedAsync(AgentKind.Cursor));
        Assert.False(await p.IsAllowedAsync(AgentKind.Codex));
    }

    [Fact]
    public async Task EeProvider_Unlicensed_AllowsAll()
    {
        var store = AllowedAgentsStore.InMemory();
        await store.ReplaceAsync(new[] { AgentKind.Claude });
        var p = new EeAllowedAgentsProvider(new FakeLicense(false), store);

        var allowed = await p.GetAllowedAsync();
        Assert.Equal(Enum.GetValues<AgentKind>().Length, allowed.Count);
        Assert.True(await p.IsAllowedAsync(AgentKind.Cursor));
    }

    [Fact]
    public async Task Store_EmptyReplace_ClearsRestriction()
    {
        var store = AllowedAgentsStore.InMemory();
        await store.ReplaceAsync(new[] { AgentKind.Claude });
        Assert.False(await store.IsUnrestrictedAsync());

        await store.ReplaceAsync(Array.Empty<AgentKind>());
        Assert.True(await store.IsUnrestrictedAsync());
        Assert.Empty(await store.ListAsync());
    }

    [Fact]
    public async Task AdminGet_Returns402_WithoutLicense()
    {
        var controller = CreateController(licensed: false, admin: true, AllowedAgentsStore.InMemory());
        var response = await controller.Get(CancellationToken.None);
        var paymentRequired = Assert.IsType<ObjectResult>(response);
        Assert.Equal(StatusCodes.Status402PaymentRequired, paymentRequired.StatusCode);
    }

    [Fact]
    public async Task AdminGet_ForbidsNonAdmin()
    {
        var controller = CreateController(licensed: true, admin: false, AllowedAgentsStore.InMemory());
        var response = await controller.Get(CancellationToken.None);
        Assert.IsType<ForbidResult>(response);
    }

    [Fact]
    public async Task AdminPutGet_RoundTripsWhitelist()
    {
        var store = AllowedAgentsStore.InMemory();
        var controller = CreateController(licensed: true, admin: true, store);

        var put = await controller.Put(
            new AllowedAgentsAdminController.SetAllowedAgentsReq(new[] { "Claude", "OpenClaw" }),
            CancellationToken.None);
        var putOk = Assert.IsType<OkObjectResult>(put);
        var putBody = Assert.IsType<AllowedAgentsAdminController.AllowedAgentsResponse>(putOk.Value);
        Assert.Equal(new[] { "Claude", "OpenClaw" }, putBody.Agents);

        var get = await controller.Get(CancellationToken.None);
        var getOk = Assert.IsType<OkObjectResult>(get);
        var getBody = Assert.IsType<AllowedAgentsAdminController.AllowedAgentsResponse>(getOk.Value);
        Assert.Equal(new[] { "Claude", "OpenClaw" }, getBody.Agents);

        var provider = new EeAllowedAgentsProvider(new FakeLicense(true), store);
        Assert.False(await provider.IsAllowedAsync(AgentKind.Cursor));
    }

    [Fact]
    public async Task AdminPut_Empty_ClearsRestriction()
    {
        var store = AllowedAgentsStore.InMemory();
        await store.ReplaceAsync(new[] { AgentKind.Claude });
        var controller = CreateController(licensed: true, admin: true, store);

        var put = await controller.Put(
            new AllowedAgentsAdminController.SetAllowedAgentsReq(Array.Empty<string>()),
            CancellationToken.None);
        var putOk = Assert.IsType<OkObjectResult>(put);
        var body = Assert.IsType<AllowedAgentsAdminController.AllowedAgentsResponse>(putOk.Value);
        Assert.Empty(body.Agents);

        var provider = new EeAllowedAgentsProvider(new FakeLicense(true), store);
        Assert.True(await provider.IsAllowedAsync(AgentKind.Cursor));
    }

    [Fact]
    public async Task AdminPut_RejectsUnknownAgent()
    {
        var controller = CreateController(licensed: true, admin: true, AllowedAgentsStore.InMemory());
        var response = await controller.Put(
            new AllowedAgentsAdminController.SetAllowedAgentsReq(new[] { "NotAnAgent" }),
            CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(response);
    }

    private static AllowedAgentsAdminController CreateController(
        bool licensed, bool admin, AllowedAgentsStore store)
    {
        var access = BuildAccess(admin ? "alice" : "bob");
        // Non-admin caller is "mallory"; admin caller is "alice".
        var owner = admin ? "alice" : "mallory";
        var controller = new AllowedAgentsAdminController(
            new FakeLicense(licensed), access, store)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        new[] { new Claim("preferred_username", owner) }, "test"))
                }
            }
        };
        return controller;
    }

    private static AdminAccess BuildAccess(string admins)
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Ee:Admins"] = admins })
            .Build();
        return new AdminAccess(cfg, NullLogger<AdminAccess>.Instance, Array.Empty<IAdminRoleProvider>());
    }

    private sealed class FakeLicense(bool enabled) : IEnterpriseLicense
    {
        public LicenseStatus Status => enabled
            ? new LicenseStatus { Valid = true }
            : new LicenseStatus { Valid = false, Reason = "test" };
        public bool Enabled => enabled;
        public Task ReloadAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
