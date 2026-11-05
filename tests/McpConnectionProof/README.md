# HTTP MCP setup and real OAuth client (#429)

This proof runs the complete production backend, OpenIddict, Postgres client/key stores,
OIDC handler and MCP transport. A real MCP SDK StreamableHTTPClientTransport discovers and
registers its OAuth client, and real Chromium performs browser sign-in and explicit consent.
The only stand-ins are a local OIDC provider with synthetic alice identity, a TLS ingress and
an empty Kubernetes list API. No production identity-provider account is used.

## Run

Use a temporary directory outside Git, .NET 10, local Postgres and Chromium with its libraries.
Create database `proof429` (or set PROOF_POSTGRES). Build `backend` to a temporary output dir.

1. `bash tests/McpConnectionProof/setup.sh <temporary-dir>` generates temporary TLS files.
2. The Kubernetes SDK requires `~/.kube/config`. In an isolated test home, copy the generated
   kubeconfig there. Do not replace an existing real kubeconfig. Setting only KUBECONFIG is
   insufficient for the constructors used by this backend SDK version.
3. `PROOF_DIR=<temporary-dir> node tests/McpConnectionProof/servers.mjs` starts the local
   OIDC provider/TLS ingress on 18443 and Kubernetes list stand-in on 18081.
4. `DOTNET=<dotnet> bash tests/McpConnectionProof/backend.sh <temporary-dir> <backend.dll>`
   starts the production backend on 18082. Set SSL_CERT_FILE only for this test process as
   the script does; never disable global TLS validation.
5. Set NODE_EXTRA_CA_CERTS to `<temporary-dir>/certs/server.crt`, PW_MODULE to the absolute
   playwright-core module path, CHROME to Chromium, and optionally RUNTIME to a directory
   whose node_modules contains @modelcontextprotocol/sdk (default /opt/session-agent).
   Run `node tests/McpConnectionProof/client.mjs`.

The script asserts the canonical URL in /api/config, the unauthenticated initialize challenge,
SDK dynamic registration/PKCE, OIDC code exchange (the stand-in validates PKCE), Chromium
login and antiforgery-protected consent, returned OAuth state, the real access token's alice
subject/resource audience, MCP initialization, tools/list and session_list. No tokens or
certificates are printed or committed. Generated files/logs belong outside Git.

This proves the protocol with the real SDK, not a vendor desktop application or a production
OIDC installation. Claude Desktop/Code and Cursor UI setup formats are covered by frontend
tests and checked against their client documentation; those applications were not signed in.
The endpoint's default remains off; insecureDev can issue a dev token and missing OIDC alone
is not a fail-closed safety argument. See docs/mcp-server.md for the decision.
