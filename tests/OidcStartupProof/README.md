# OIDC startup proof

Run `dotnet run --project tests/OidcStartupProof` with the .NET 10 SDK.
The executable starts a real discovery/JWKS HTTP issuer and real ASP.NET bearer hosts.
It reproduces the old per-request 500 for HTTP metadata with the default HTTPS policy,
then checks startup refusal, unreachable discovery refusal, explicit HTTP opt-in, and
an authenticated request using the issuer's actual signed JWT. No credentials or output
are written into the repository. This focused proof complements the real install proof;
it does not exercise browser login, TLS ingress or an external identity service.
