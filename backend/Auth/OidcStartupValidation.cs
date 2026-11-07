using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;

namespace AgentHub.Api.Auth;

public static class OidcStartupValidation
{
    public static async Task ValidateAsync(IServiceProvider services)
    {
        try
        {
            // Resolve the handler's own manager so startup exercises the same HTTPS policy,
            // discovery document and signing keys that will authenticate real requests.
            var options = services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
                .Get(JwtBearerDefaults.AuthenticationScheme);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var metadata = await options.ConfigurationManager!.GetConfigurationAsync(timeout.Token);
            if (string.IsNullOrWhiteSpace(metadata.Issuer) || metadata.SigningKeys.Count == 0)
                throw new InvalidOperationException("Discovery did not provide an issuer and signing keys.");
        }
        catch (InvalidOperationException) { throw Failure(); }
        catch (ArgumentException) { throw Failure(); }
        catch (FormatException) { throw Failure(); }
        catch (HttpRequestException) { throw Failure(); }
        catch (IOException) { throw Failure(); }
        catch (OperationCanceledException) { throw Failure(); }
    }

    // Discovery errors may carry remote response contents. Keep expected configuration and
    // transport failures actionable without exposing them; programming errors still propagate.
    private static HubAuthConfigurationException Failure() => new(
        "OIDC startup validation failed. Check oidc.authority, issuer discovery and signing-key "
        + "reachability from the backend, DNS and TLS trust. HTTPS metadata is required by default; "
        + "an isolated HTTP test issuer needs oidc.requireHttpsMetadata=false. "
        + "The backend has stopped without accepting requests.");
}
