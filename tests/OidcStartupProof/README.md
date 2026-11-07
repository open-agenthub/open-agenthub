# OIDC startup proof

Run `dotnet run --project tests/OidcStartupProof` with the .NET 10 SDK.
The executable starts a real discovery/JWKS HTTP issuer and real ASP.NET bearer hosts.
It reproduces the old per-request 500 for HTTP metadata with the default HTTPS policy,
then checks startup refusal, unreachable discovery refusal, explicit HTTP opt-in, and
an authenticated request using the issuer's actual signed JWT. No credentials or output
are written into the repository. This focused proof complements the real install proof;
it does not exercise browser login, TLS ingress or an external identity service.

## Real quickstart release

`install.sh` runs after the install-smoke workflow's real quickstart and upgrade-in-place.
It starts a local public-client identity stand-in with discovery, JWKS, S256 code exchange
and rotating offline refresh tokens. The real release refuses an HTTP issuer with HTTPS
metadata required and an unreachable issuer; each failed upgrade is recovered with Helm
rollback. Explicit HTTP opt-in then becomes ready and an exchanged/refreshed access token
reaches the real Admin API. Finally the script restores the original API-key quickstart.
This test-only HTTP setup does not prove public TLS ingress or an external provider's UI.
The stand-in uses the protocol/claims expected from a compatible identity broker; it is
not a verification of every provider's own client configuration.

`keycloak-install.sh` additionally reuses the #480 audit's pinned identity image and realm
fixture, replaces the users with a generated test-only team-admin password, disables password
grants, and drives the actual frontend login and callback in Chromium. It verifies a refresh
token, actual refresh exchange and authenticated admin requests before restoring quickstart.
It runs after the protocol proof in install-smoke. Generated credentials and output are ephemeral.
