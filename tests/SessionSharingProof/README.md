# Session sharing HTTP and Chromium proof (#328)

The host uses production sharing/session controllers, sharing/access services,
KubernetesSessionService and real Postgres session/user/project/share stores. Kubernetes reads
use a real rootless API server (no kubelet). The proof seeds paused sessions and a database
transcript. Browser summaries are fixed at Stopped; the ephemeral MCP registry is empty and
object storage disabled. No production cluster, sessions or credentials are used.

`identity.mjs` is a local OIDC protocol fixture: Alice and Bob, random RSA signing keys, one-use
codes, PKCE verification, signed ID/access tokens. The real frontend OIDC client and backend JWT
handler perform login and validation. Accounts are synthetic, recorded through UserDirectory;
this does not establish an external provider's behavior. Negative HTTP tests also use a host-only
cookie authentication handler.

## Setup and run

Install .NET 10, rootless Postgres and the rootless API-server rig following their skills.
Create database proof328 at 127.0.0.1:54329 (user postgres) and namespace sharing-proof in the
isolated API server. Install frontend dependencies and playwright-core.

```sh
export PROOF_KUBECONFIG=<isolated-kubeconfig>
export PROOF_ISSUER=http://localhost:5173/__identity
export UI=http://localhost:5173
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
node tests/SessionSharingProof/identity.mjs
# Separate process:
dotnet run --project tests/SessionSharingProof/Host.csproj
# Separate process:
./frontend/node_modules/.bin/vite --config tests/SessionSharingProof/vite.config.mjs --host 0.0.0.0 --port 5173
# Browser proof:
CDP=<Chromium-endpoint> RELAY=http://127.0.0.1:5173 PW_MODULE=<playwright-core-module> node tests/SessionSharingProof/browser.cjs
```

The visible browser runs in another pod. The optional relay forwards its loopback-origin HTTP
requests to the actual Vite/backend host, preserving cookies, headers, bodies and redirects.
This supplies the secure context required by Chromium's real WebCrypto PKCE implementation;
it does not fabricate responses. A reachable HTTPS test origin can run without the relay.
WebSockets are not relayed; paused-session checks do not require them.

Restart the host with Sharing__UserSuggestionsEnabled=false, then run
`node tests/SessionSharingProof/exact-mode.cjs` to verify exact-only lookup and the real HTTP 429.
Run `HELM=<helm-binary> node tests/SessionSharingProof/chart.cjs` for chart default/false checks.
ShareRecipientSearchPostgresTests covers the result cap, literal wildcards, minimum prefix length,
self exclusion, exact email and opt-out; enable Postgres tests with
AGENTHUB_TEST_POSTGRES_CONNECTION_STRING.

## Baseline and results

Current-main baseline f168837 was built in a separate worktree on proof/328-baseline. Copy the
proof host into that tree, remove its two recipient-rate-limiter registration/middleware calls
(absent on baseline), and build against its backend. Run its frontend on port 5173 and host
without PROOF_ISSUER, then run named-baseline.cjs with a browser-reachable UI origin.
The grant exists in /api/sessions/shared, but metadata/conversation/transcript return 404 to Bob.
Chromium's named deep link falls to Home with no Shared with me group. baseline.cjs separately
confirms a generated bearer link succeeds on current main.

Fixed branch: 25 browser/HTTP checks pass: Alice selects Bob from actual autocomplete, the
copied link is absolute, Bob's Code+PKCE login returns to the named session with its query,
the grant is discoverable and transcript readable, metadata sanitized, wrong users/foreign
sessions denied, named grant revocable, bearer access anonymous, invalid/revoked links explained.
The opt-out/rate-limit proof passes three checks; both chart renders pass. Results and browser
screenshots are alongside the scripts.

Not established: the owner's deployed public-host/proxy configuration or original bearer URL;
an external production identity provider; a live terminal, browser workload or kubelet. The
existing SPA/API/auth callback/ingress serve at the origin root and do not support subpaths.
See docs/session-sharing-api.md for decisions and rejected alternatives.
