# OIDC startup validation

The API resolves its actual bearer handler configuration and fetches discovery and signing
keys before schema initialization and before listening. The same manager caches the initial
configuration and retains its normal refresh behavior. Discovery failure is fatal after a
20-second bound; the operator sees one configuration error without remote response contents.

Rejected: discovering on the first request leaves a nominally started backend returning
errors for every caller and makes Helm wait for readiness until the upgrade times out.
Rejected: a separate HTTP probe would not exercise the handler's HTTPS policy or JWKS fetch.
A temporary issuer outage therefore prevents a fresh replica starting; existing replicas keep
the handler's cached configuration and normal refresh behavior. Operators should restore issuer
availability before restarting all replicas.

The frontend always adds offline_access, so client configuration must permit refresh tokens.
Provider support is defined by JWT access tokens, discovery, browser CORS and stable owner
claims rather than an unverified list of brands. Opaque tokens and ID-token-only validation
are outside this implementation.
