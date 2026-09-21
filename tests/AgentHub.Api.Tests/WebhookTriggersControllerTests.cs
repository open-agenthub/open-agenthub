using System.Security.Claims;
using AgentHub.Api.Controllers;
using AgentHub.Api.Models;
using AgentHub.Api.Webhooks;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentHub.Api.Tests;

public class WebhookTriggersControllerTests
{
    private const string Origin = "https://hub.example.test";

    [Fact]
    public async Task Create_ReturnsSecretOnceAndDeliveryUrl_StoresOnlyProtectedSecret()
    {
        var store = new InMemoryWebhookTriggerStore();
        var controller = Controller(store);

        var result = await controller.Create(new CreateWebhookTriggerRequest
        {
            Name = " review-bot ",
            ProviderId = "gitlab",
            PromptTemplate = "Review {{title}}"
        }, CancellationToken.None);

        var created = Assert.IsType<CreatedWebhookTrigger>(
            Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.StartsWith("whs_", created.Secret);
        Assert.Equal(68, created.Secret.Length); // "whs_" + 32 bytes hex
        Assert.Equal($"{Origin}/api/git/webhooks/{created.Trigger.Id}", created.Trigger.Url);
        Assert.Equal("review-bot", created.Trigger.Name);
        Assert.Equal(WebhookTriggerEvents.Default, created.Trigger.Events);

        var record = Assert.Single(store.Records);
        Assert.Equal("alice", record.Owner);
        Assert.Equal("protected:" + created.Secret, record.SecretProtected);
    }

    [Fact]
    public async Task Create_NormalizesEvents()
    {
        var store = new InMemoryWebhookTriggerStore();
        var controller = Controller(store);

        var result = await controller.Create(new CreateWebhookTriggerRequest
        {
            Name = "t", PromptTemplate = "p", Events = [" Opened ", "opened", "MERGED"]
        }, CancellationToken.None);

        var created = Assert.IsType<CreatedWebhookTrigger>(
            Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(new[] { "opened", "merged" }, created.Trigger.Events);
    }

    [Theory]
    [InlineData("", "prompt")]
    [InlineData("   ", "prompt")]
    [InlineData("name", "")]
    [InlineData("name", "  ")]
    public async Task Create_MissingNameOrTemplate_ReturnsBadRequest(string name, string template)
    {
        var controller = Controller(new InMemoryWebhookTriggerStore());

        var result = await controller.Create(new CreateWebhookTriggerRequest
        {
            Name = name, PromptTemplate = template
        }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Create_UnknownEvent_ReturnsBadRequest()
    {
        var controller = Controller(new InMemoryWebhookTriggerStore());

        var result = await controller.Create(new CreateWebhookTriggerRequest
        {
            Name = "t", PromptTemplate = "p", Events = ["deployed"]
        }, CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("deployed", (string)bad.Value!);
    }

    [Fact]
    public async Task List_ReturnsOnlyOwnTriggers_WithDeliveryUrls()
    {
        var store = new InMemoryWebhookTriggerStore();
        await store.CreateAsync("alice", new CreateWebhookTriggerRequest { Name = "mine", PromptTemplate = "p" }, "protected:x");
        await store.CreateAsync("bob", new CreateWebhookTriggerRequest { Name = "theirs", PromptTemplate = "p" }, "protected:y");
        var controller = Controller(store);

        var list = await controller.List(CancellationToken.None);

        var trigger = Assert.Single(list);
        Assert.Equal("mine", trigger.Name);
        Assert.Equal($"{Origin}/api/git/webhooks/{trigger.Id}", trigger.Url);
    }

    [Fact]
    public async Task Delete_OwnTrigger_RemovesIt_ForeignOrMissing_Returns404()
    {
        var store = new InMemoryWebhookTriggerStore();
        await store.CreateAsync("alice", new CreateWebhookTriggerRequest { Name = "mine", PromptTemplate = "p" }, "protected:x");
        await store.CreateAsync("bob", new CreateWebhookTriggerRequest { Name = "theirs", PromptTemplate = "p" }, "protected:y");
        var mine = store.Records[0].Info.Id;
        var theirs = store.Records[1].Info.Id;
        var controller = Controller(store);

        Assert.IsType<NotFoundResult>(await controller.Delete(theirs, CancellationToken.None));
        Assert.IsType<NoContentResult>(await controller.Delete(mine, CancellationToken.None));
        Assert.IsType<NotFoundResult>(await controller.Delete(mine, CancellationToken.None));
        var remaining = Assert.Single(store.Records);
        Assert.Equal("bob", remaining.Owner);
    }

    [Fact]
    public void DataProtectionProtector_RoundTripsWithoutLeakingPlaintext()
    {
        var services = new ServiceCollection();
        services.AddDataProtection();
        var protector = new WebhookSecretProtector(
            services.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>());

        const string secret = "whs_super-secret-value";
        var stored = protector.Protect(secret);

        Assert.DoesNotContain("super-secret-value", stored);
        Assert.Equal(secret, protector.Unprotect(stored));
    }

    private static WebhookTriggersController Controller(InMemoryWebhookTriggerStore store)
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["FrontendOrigin"] = Origin })
            .Build();
        var controller = new WebhookTriggersController(store, new PlainWebhookSecretProtector(), cfg)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.HttpContext.User = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim("preferred_username", "alice")], "test"));
        return controller;
    }
}
