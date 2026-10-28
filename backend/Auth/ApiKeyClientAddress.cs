using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;

namespace AgentHub.Api.Auth;

public static class ApiKeyClientAddress
{
    public const string ItemKey = "agenthub.apiKey.clientAddress";

    public static IApplicationBuilder UseApiKeyClientAddress(this IApplicationBuilder app, IConfiguration configuration)
    {
        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor,
            ForwardLimit = 1
        };
        // Empty lists must mean trust nobody, not the middleware's special trust-everyone mode.
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        foreach (var value in (configuration["ForwardedHeaders:KnownProxies"] ?? "127.0.0.1,::1").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            options.KnownProxies.Add(IPAddress.Parse(value));
        foreach (var value in (configuration["ForwardedHeaders:KnownNetworks"] ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(value));

        var middleware = new ForwardedHeadersMiddleware(_ => Task.CompletedTask,
            app.ApplicationServices.GetRequiredService<ILoggerFactory>(), Options.Create(options));
        return app.Use(async (context, next) =>
        {
            var socketAddress = context.Connection.RemoteIpAddress;
            var socketPort = context.Connection.RemotePort;
            if (options.KnownProxies.Count > 0 || options.KnownIPNetworks.Count > 0)
            {
                await middleware.Invoke(context);
            }
            var clientAddress = context.Connection.RemoteIpAddress;
            // Pod authorization must continue to identify the direct peer, never an ingress user.
            context.Connection.RemoteIpAddress = socketAddress;
            context.Connection.RemotePort = socketPort;
            if (socketAddress is not null && clientAddress is not null && !clientAddress.Equals(socketAddress))
            {
                var guard = context.RequestServices.GetService<IForwardedPeerGuard>();
                if (guard is not null && !await guard.CanForwardAsync(socketAddress, context.RequestAborted))
                    clientAddress = socketAddress;
            }
            context.Items[ItemKey] = clientAddress?.ToString() ?? "unknown";
            await next(context);
        });
    }
}
