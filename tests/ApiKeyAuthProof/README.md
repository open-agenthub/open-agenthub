# API key for installs without OIDC — proof (#324)

Everything on the path is the real thing; nothing is mocked:

| Piece | What runs |
|---|---|
| Backend | the real `AgentHub.Api`, started by `backend.mjs` in each auth mode, on 127.0.0.1:8080 (http) and :8443 (https, which the remote MCP's OAuth server requires) |
| Kubernetes | a real kube-apiserver + etcd (skill `rootless-kube-apiserver-admission-proof`), namespace `agenthub-sessions` with SA `agenthub-agent`; no kubelet, so session pods stay Pending |
| Database | real Postgres on 127.0.0.1:54329, database `proof324` (skill `rootless-postgres-backend-tests`) |
| Browser | the session's real Chromium over CDP, against the branch's Vite app |
| Baseline | the v1.4.0 backend, built from a detached worktree |

Setup, with `RIG=$HOME/rig`: a self-signed certificate for 127.0.0.1 in `$RIG/tls/{cert,key}.pem`,
and a random key in `$RIG/proof-key.txt`. The scripts read the key from that file and never print it.

```bash
node proof.mjs <branch>/backend/bin/Debug/net10.0/AgentHub.Api.dll <v1.4.0>/backend/bin/Debug/net10.0/AgentHub.Api.dll
node backend.mjs <branch dll> apiKey $RIG/b.log &   # then, in frontend/: npx vite --host 0.0.0.0
CDP=http://<browser>:9222 UI=http://<pod ip>:5173 PW_DIR=<dir with playwright-core> node browser.mjs apiKey <outdir>
node backend.mjs <branch dll> insecure $RIG/b.log &  # restart the backend in insecure mode, then
CDP=... UI=... PW_DIR=... node browser.mjs insecure <outdir>
```

## Result (2026-10-27)

`proof.mjs`: 86/86 (output in `results.txt`). `browser.mjs apiKey`: 16/16. `browser.mjs insecure`: 4/4.

- **Baseline v1.4.0, no configuration:** it starts and answers anonymous `GET /api/sessions`,
  `/api/admin/overview` and `/api/admin/roles` with 200. It also runs an anonymous admin cleanup
  dry-run, which fails with 500 only because the rig has no object storage.
- **Refused startup:** with no auth configured, with `Auth:ApiKey=changeme`, with `changeme`
  repeated to 32 characters (fewer than 8 distinct characters), and with
  `Auth:InsecureDevMode=yes`, the process exits with code 1 and prints one `FATAL:` line naming the
  fix. Nothing ever listens on 8080.
- **API key:** `/api/sessions`, `/api/projects`, `/api/admin/{access,overview,roles}`, the cleanup
  dry-run and `/api/tokens` all answer 401 without a key and with a wrong one, and get through
  with the right key. A key in the query string off `/ws` is ignored, and a key one character
  short is refused. The key acts as `admin`, an admin.
- **WebSockets** `events`, `terminal` and `shell`: 401 without a key and with a wrong one. The
  right key gets 101 on `events`, both in `access_token` and in the header. On `terminal` it gets
  past authentication; the rig has no agent to reach behind it.
- **Untouched schemes:** the internal callback works with the pod's session token (taken from the
  real pod spec) and no API key. A wrong session token is refused, and the API key does not stand
  in for the session token. Personal API tokens work on `/api/remote` and are not accepted as the
  API key.
- **Remote MCP:** the full OAuth 2.1 + PKCE flow runs against the real OpenIddict server. The
  consent form asks for the key. A wrong key or no key gets 401 and the form again,
  `prompt=none` gets `login_required`, and a stored approval does not skip the key. The token from
  the flow works on `/mcp` (`initialize`, `tools/list`, `session_list` shows the owner's session).
  The API key itself is not an MCP token.
- **Throttling:** 10 wrong keys from one client each get 401. The 11th attempt gets 429 with
  `Retry-After: 60`, even with the right key, on REST, the WebSocket and the MCP consent form.
  Another client behind the same address is unaffected. 300 wrong keys from rotating clients
  through one address all get 401 and do not lock out the owner, and 30 personal-token calls do
  not count. Each wrong key is logged as `Rejected a wrong API key from 127.0.0.1 via 203.0.113.50
  for GET /api/sessions`.
- **Logs:** neither the right key nor a presented wrong one appears in any backend log. The first
  run found the key in clear text in OpenIddict's request log under the field name `api_key`. The
  field is now `password`, which OpenIddict writes as `[redacted]`.
- **Insecure mode:** a warning on start, `/api/config` reports `insecureDev`, anonymous requests
  are admin user `dev`, and the UI shows a banner that cannot be dismissed.
- **Browser:** the prompt appears before any data loads, and a wrong key is reported and not
  stored. The right key opens the app: the owner's session is listed, the session socket carries
  the key and stays open, and the admin area opens. A reload keeps the key; empty sessionStorage
  asks again. A rotated key leads back to the prompt, and sign-out forgets the key.

Not shown here: a real kubelet with a running agent behind the terminal socket (the auth decision
happens before the proxy, which the 401/upgrade results cover), and an OIDC provider (that mode's
code path is unchanged). The shutdown `ObjectDisposedException` in the logs is pre-existing: the
v1.4.0 baseline logs it too.

## Trusted forwarding regression (#327)

`forwarded.mjs` starts the real backend using the same prerequisites above. Run:

```bash
RIG=<rig directory> DOTNET=<dotnet binary> node tests/ApiKeyAuthProof/forwarded.mjs <fixed backend dll> [baseline backend dll]
```

The optional baseline must be built from main before #327. The proof reproduces rotating-XFF
limit evasion there, then checks the fixed backend with an exact trusted proxy and again with
an exact trusted network. Requests bind to separate loopback source addresses: an untrusted
peer cannot buy a fresh bucket by changing XFF, and clients behind a trusted peer have separate
buckets. Invented left entries, malformed addresses, Retry-After, another client's correct key,
an exhausted client's correct key, encoded log-forging paths and absent credentials in logs
are checked. Agent/browser pods with IPs inside the trusted ranges cannot assert XFF; creating
and deleting another pod after startup verifies that the live watch updates the exclusion. The backend's Postgres and Kubernetes API server are real; no ingress controller
or kubelet is needed for these authentication decisions. Log files remain in the rig directory,
not in Git.

Verified on 2026-10-27: 115 assertions, backend suite 2,547 passed with real Postgres and zero
skips, and `tests/helm/api-key-values.ps1` passed. The existing `proof.mjs` also passed
81/81 checks (without its optional v1.4.0 baseline), covering REST, WebSocket, personal-token,
MCP consent/OAuth and insecure-mode behavior after the forwarding change. Socket-IP preservation and empty trust lists
are also covered by the backend suite. A deployed ingress and IPv6 network were not exercised.
