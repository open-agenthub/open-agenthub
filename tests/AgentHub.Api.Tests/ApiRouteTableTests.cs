using AgentHub.Api.Controllers;
using AgentHub.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ActionConstraints;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>
/// Assertions about the route table MVC actually builds, rather than about the attributes in the
/// source. Every other controller test instantiates its controller directly and calls the method,
/// which proves nothing about whether a request can reach it: a mistyped template, a missing HTTP
/// verb or two actions claiming the same route all pass those tests and fail at runtime.
///
/// Built from the assembly's application parts, so no cluster, database or object storage is
/// involved — action descriptors come from metadata, not from the controllers' dependencies.
/// </summary>
public class ApiRouteTableTests
{
    /// <summary>Routes a released client depends on. The session-transfer plugin calls exactly
    /// these, so a rename here is a breaking change for an installed copy of it, not a
    /// refactor.</summary>
    public static TheoryData<string, string> RemoteTransferRoutes => new()
    {
        { "GET", "api/remote/sessions" },
        { "GET", "api/remote/sessions/{id}" },
        { "POST", "api/remote/sessions" },
        { "POST", "api/remote/sessions/{id}/pause" },
        { "POST", "api/remote/sessions/{id}/resume" },
        { "GET", "api/remote/sessions/{id}/state" },
        { "PUT", "api/remote/sessions/{id}/state" },
        { "DELETE", "api/remote/sessions/{id}" },
        // The stdio MCP server and docs/session-expiry.md name this one for changing a deadline.
        { "PATCH", "api/remote/sessions/{id}" }
    };

    [Theory]
    [MemberData(nameof(RemoteTransferRoutes))]
    public void RemoteSurface_ExposesEveryRouteTheTransferFlowNeeds(string method, string template)
    {
        var matching = Routes().Where(route => route.Template == template && route.Methods.Contains(method)).ToList();

        Assert.True(matching.Count > 0,
            $"{method} {template} is not in the route table. Templates found at that path: "
            + string.Join(", ", Routes().Where(r => r.Template == template)
                .Select(r => $"{string.Join('|', r.Methods)} {r.Template}")));
    }

    /// <summary>The sharing routes on the token surface, which the stdio MCP server's client
    /// calls by path (mcp/agenthub/client.mjs) — the same contract as the transfer routes.</summary>
    public static TheoryData<string, string> RemoteSharingRoutes => new()
    {
        { "GET", "api/remote/sessions/shared" },
        { "GET", "api/remote/sessions/{id}/shares" },
        { "POST", "api/remote/sessions/{id}/shares/users" },
        { "DELETE", "api/remote/sessions/{id}/shares/users/{recipient}" },
        { "POST", "api/remote/sessions/{id}/shares/links" },
        { "DELETE", "api/remote/sessions/{id}/shares/links/{linkId}" }
    };

    [Theory]
    [MemberData(nameof(RemoteSharingRoutes))]
    public void RemoteSurface_ExposesEverySharingRoute(string method, string template)
    {
        Assert.Contains(Routes(), route => route.Template == template && route.Methods.Contains(method));
    }

    /// <summary>Routes the credential-scope feature adds (docs/credential-scopes.md); the stdio
    /// MCP server and the token settings page call exactly these.</summary>
    public static TheoryData<string, string> CredentialScopeRoutes => new()
    {
        { "GET", "api/remote/credentials" },
        { "PATCH", "api/tokens/{id}" }
    };

    [Theory]
    [MemberData(nameof(CredentialScopeRoutes))]
    public void CredentialScopeSurface_ExposesItsRoutes(string method, string template)
    {
        Assert.Contains(Routes(), route => route.Template == template && route.Methods.Contains(method));
    }

    [Fact]
    public void SharedWithMe_ExistsOnBothSurfaces_AndTheInAppOneIsBehindTheLogin()
    {
        // The in-app listing lives on the Enterprise controller with an absolute template, so it
        // is outside the SessionsController sweep above and needs its own [Authorize] check: an
        // unauthenticated principal would otherwise be asked "what is shared with you".
        var inApp = Actions().OfType<ControllerActionDescriptor>()
            .Single(action => action.AttributeRouteInfo?.Template == "api/sessions/shared");
        Assert.Contains(inApp.EndpointMetadata, metadata => metadata is AuthorizeAttribute);
        Assert.DoesNotContain(inApp.EndpointMetadata, metadata => metadata is AllowAnonymousAttribute);
        Assert.Contains("GET", HttpMethodsOf(inApp));

        Assert.Contains(Routes(), route => route.Template == "api/remote/sessions/shared" && route.Methods.Contains("GET"));
    }

    [Fact]
    public void StateRoutes_ExistOnBothSurfaces_BecauseATokenCannotReachTheInAppOne()
    {
        // api/sessions needs an interactive login, so a workstation CLI can only use api/remote;
        // the web app can only use api/sessions. Losing either one locks out one of the two.
        foreach (var prefix in new[] { "api/sessions", "api/remote/sessions" })
        {
            Assert.Contains(Routes(), route =>
                route.Template == $"{prefix}/{{id}}/state" && route.Methods.Contains("GET"));
            Assert.Contains(Routes(), route =>
                route.Template == $"{prefix}/{{id}}/state" && route.Methods.Contains("PUT"));
        }
    }

    [Fact]
    public void ConvertRoute_ExistsOnBothSurfacesAndForDescendantsInThePod()
    {
        // The web card uses api/sessions, a token client api/remote, and an orchestrating agent
        // the internal peer route — three callers, three auth schemes, one service method.
        foreach (var template in new[]
                 {
                     "api/sessions/{id}/convert",
                     "api/remote/sessions/{id}/convert",
                     "internal/sessions/{id}/peer/{childId}/convert"
                 })
        {
            Assert.Contains(Routes(), route => route.Template == template && route.Methods.Contains("POST"));
        }
    }

    [Fact]
    public void NoTwoActions_ClaimTheSameMethodAndTemplate()
    {
        // An ambiguous route throws when the first request arrives at it, not at startup, so it
        // can ship. Checked across the whole application, not only the controllers touched here.
        var duplicates = Routes()
            .SelectMany(route => route.Methods.Select(method => (Key: $"{method} {route.Template}", route.Action)))
            .GroupBy(entry => entry.Key)
            .Where(group => group.Select(entry => entry.Action).Distinct().Count() > 1)
            .Select(group => $"{group.Key} -> {string.Join(", ", group.Select(entry => entry.Action).Distinct())}")
            .ToList();

        Assert.Empty(duplicates);
    }

    [Fact]
    public void EveryRemoteAction_ResolvesItsOwnTokenAndIsNotBehindTheLoginFilter()
    {
        // RemoteController authenticates the Authorization header itself. An [Authorize] on one of
        // its actions would reject a valid personal token before that code ran, with a 401 that no
        // amount of correct token handling could fix.
        var remote = Actions().OfType<ControllerActionDescriptor>()
            .Where(action => action.AttributeRouteInfo?.Template?.StartsWith("api/remote") == true)
            .ToList();

        Assert.NotEmpty(remote);
        foreach (var action in remote)
        {
            Assert.DoesNotContain(action.EndpointMetadata, metadata => metadata is AuthorizeAttribute);
            Assert.Contains(action.EndpointMetadata, metadata => metadata is AllowAnonymousAttribute);
        }
    }

    [Fact]
    public void EverySessionsAction_IsBehindTheLoginFilter()
    {
        // The in-app surface takes the owner from the signed-in user. An action that slipped out
        // from under [Authorize] would read that claim from an unauthenticated principal.
        var inApp = Actions().OfType<ControllerActionDescriptor>()
            .Where(action => action.ControllerTypeInfo.AsType() == typeof(SessionsController))
            .ToList();

        Assert.NotEmpty(inApp);
        foreach (var action in inApp)
        {
            Assert.Contains(action.EndpointMetadata, metadata => metadata is AuthorizeAttribute);
            Assert.DoesNotContain(action.EndpointMetadata, metadata => metadata is AllowAnonymousAttribute);
        }
    }

    [Fact]
    public void BothStateUploads_RaiseTheRequestSizeLimit()
    {
        // Without the attribute Kestrel's 30 MB default applies and a larger archive is rejected
        // before the handler runs, with nothing in the response naming size as the reason.
        var uploads = Actions().OfType<ControllerActionDescriptor>()
            .Where(action => action.AttributeRouteInfo?.Template?.EndsWith("/state") == true
                && HttpMethodsOf(action).Contains("PUT"))
            .ToList();

        Assert.Equal(2, uploads.Count);
        foreach (var action in uploads)
        {
            Assert.Contains(action.FilterDescriptors,
                filter => filter.Filter is RequestSizeLimitAttribute);
        }
    }

    [Fact]
    public void TheDeclaredArchiveLimit_IsAboveKestrelsDefault()
    {
        // The paired invariant to the test above: raising the limit only helps if the value is
        // actually larger than the default it replaces.
        const long kestrelDefault = 30L * 1024 * 1024;

        Assert.True(SessionStateTransfer.MaxArchiveBytes > kestrelDefault,
            $"{SessionStateTransfer.MaxArchiveBytes} does not exceed Kestrel's {kestrelDefault}");
    }

    [Fact]
    public void EveryController_CanBeConstructedByTheActivator()
    {
        // A controller is built by ActivatorUtilities with no explicit arguments, so two public
        // constructors that both match — the usual shape being a DI one beside a test seam — make
        // the choice ambiguous. It throws on the first request rather than at startup, every route
        // on that controller answers 500 with an empty body, and the suite stays green because the
        // tests call the seam directly and never go through activation. One such mistake shipped
        // and took the whole api/remote surface, and with it the session-transfer plugin, offline.
        var failures = new List<string>();
        foreach (var controller in Actions().OfType<ControllerActionDescriptor>()
                     .Select(action => action.ControllerTypeInfo.AsType())
                     .Distinct())
        {
            // The ambiguity, and a controller with no usable constructor at all, both arrive as
            // InvalidOperationException. Anything else is not a wiring problem this test can
            // describe, so it is left to fail the run on its own terms.
            try { ActivatorUtilities.CreateFactory(controller, Type.EmptyTypes); }
            catch (InvalidOperationException e) { failures.Add($"{controller.Name}: {e.Message}"); }
        }

        Assert.Empty(failures);
    }

    // ------------------------------------------------------------------ fixtures

    private sealed record Route(string Template, IReadOnlySet<string> Methods, string Action);

    private static IReadOnlyList<Route> Routes() => Cached.Routes;

    private static IReadOnlyList<ActionDescriptor> Actions() => Cached.Actions;

    private static IReadOnlySet<string> HttpMethodsOf(ActionDescriptor action) =>
        (action.ActionConstraints ?? [])
            .OfType<HttpMethodActionConstraint>()
            .SelectMany(constraint => constraint.HttpMethods)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Discovery walks every controller in the assembly, so it is done once.</summary>
    private static class Cached
    {
        internal static readonly IReadOnlyList<ActionDescriptor> Actions = Discover();

        internal static readonly IReadOnlyList<Route> Routes = Actions
            .Where(action => action.AttributeRouteInfo?.Template is not null)
            .Select(action => new Route(
                action.AttributeRouteInfo!.Template!,
                HttpMethodsOf(action),
                action is ControllerActionDescriptor controller
                    ? $"{controller.ControllerName}.{controller.ActionName}"
                    : action.DisplayName ?? "?"))
            .ToList();

        private static IReadOnlyList<ActionDescriptor> Discover()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddControllers().AddApplicationPart(typeof(RemoteController).Assembly);
            var provider = services.BuildServiceProvider();
            return provider.GetRequiredService<IActionDescriptorCollectionProvider>().ActionDescriptors.Items;
        }
    }
}
