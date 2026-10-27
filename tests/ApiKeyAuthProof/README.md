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

`proof.mjs`: 81/81 (output in `results.txt`). `browser.mjs apiKey`: 16/16. `browser.mjs insecure`: 4/4.

- **Baseline v1.4.0, no configuration:** it starts and answers anonymous `GET /api/sessions`,
  `/api/admin/overview` and `/api/admin/roles` with 200. It also runs an anonymous admin cleanup
  dry-run, which fails with 500 only because the rig has no object storage.
- **Refused startup:** with no auth configured, with `Auth:ApiKey=changeme`, and with
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
  Another client behind the same address is unaffected, and 30 personal-token calls do not
  count. Each wrong key is logged as `Rejected a wrong API key from 127.0.0.1 via 203.0.113.50
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
