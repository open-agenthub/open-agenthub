# Installs without OIDC: an API key, and no open default

Until 1.4.0 an empty `oidc.authority`, which is the chart default, put the backend in a
"development" mode: every request was authenticated as user `dev`, and with an empty `Ee:Admins`
that user was an admin. Anyone who could reach the backend could call every API, including the
admin endpoints, the terminals and the session files. Nothing stopped a production install from
running that way (issue #324). This file records what replaced it and what was turned down.

## Three modes, and none of them is implicit

`HubAuthOptions.Resolve` picks the mode at startup. First match wins:

| Configuration | Mode | Who a request is |
|---|---|---|
| `Oidc:Authority` set | `oidc` | the token's `preferred_username` (unchanged) |
| `Auth:ApiKey` set (at least 32 characters, at least 8 distinct) | `apiKey` | `Auth:ApiKeyOwner`, default `admin` |
| `Auth:InsecureDevMode=true` | `insecureDev` | `dev`, as before |
| nothing | — | the backend exits with one `FATAL:` line naming the three options |

**Fail closed rather than default to the API key.** The backend could generate a key itself when
nothing is configured. It would then have to store it somewhere the owner can find it, and with
two replicas both would generate a different one. The chart can generate a key and keep it in a
Secret, so the chart does that, and the backend only refuses to run without one.

**The insecure mode needs an explicit `true`.** An empty or misspelt value (`yes`) counts as not
opted in. The backend logs a warning on every start, the chart prints one in `NOTES.txt`, and the
UI shows a banner that cannot be dismissed, because an install left open is noticed in the logs
or on screen and nowhere else. `values-dev.yaml` (Docker Desktop) sets it.

**A key shorter than 32 characters, or with fewer than 8 distinct characters, stops startup.**
Such a value is much more likely a placeholder than a key, and starting with it would look secured
without being so. See the next section for why this check, not the rate limit, carries the defence.

## The key

- **Where it travels:** exactly where an OIDC access token does. It goes in
  `Authorization: Bearer` on REST and in `access_token` on `/ws/*`, because browser WebSockets
  cannot set headers. The frontend's existing token plumbing therefore carries it with no second
  code path. On any other path `access_token` is ignored, so a key in the URL cannot show up in
  access logs outside the WebSocket routes, which already took the OIDC token that way.
- **Comparison:** SHA-256 of both sides and `CryptographicOperations.FixedTimeEquals`. Comparing
  the raw strings in constant time still returns early when the lengths differ. Only the hash is
  kept in memory. A wrong key fails with the fixed text `Invalid API key`, never the value.
- **No credential is `NoResult`, not a failure.** Internal callbacks (`X-Agent-Token`), the
  remote API with personal API tokens, the skill-library MCP and the catalog MCP gateway are
  `[AllowAnonymous]` and check their own tokens, some of which arrive as `Authorization: Bearer`.
  `/mcp` authenticates with the OpenIddict validation scheme named in its policy. None of these
  is affected by the default scheme, and none was changed.
- **In the browser:** `sessionStorage`, so the key ends with the browser session. `localStorage`
  would leave a long-lived admin credential on disk for every later user of the profile. The
  prompt checks the key against `GET /api/admin/access` before keeping it, and that call also
  returns the owner's name, which a static key cannot carry the way a token's profile does. A
  401 later (after rotation) drops the key and shows the prompt again.

## Throttling wrong keys, and why entropy carries the defence

**The key's entropy is what stops brute force.** Startup refuses a key shorter than 32 characters
or with fewer than 8 distinct characters, such as `changeme` repeated or a date. A random hex key
of 32 characters has about 14 distinct ones; two million samples never had fewer than 8. The chart
generates 48 random alphanumerics.

On top of that, after 10 wrong keys from one client within a minute, every key from that client
gets 429 with `Retry-After: 60` until the minute ends. This applies to the REST API, the
WebSockets and the MCP consent form. The right key is throttled too, for that client only;
otherwise the limit would not slow that client's guessing.

- **Which client.** ASP.NET Forwarded Headers reads one `X-Forwarded-For` entry from the
  right only when the socket peer matches `ForwardedHeaders:KnownProxies` or
  `ForwardedHeaders:KnownNetworks`. Otherwise the socket address is the client, regardless of
  invented headers. The validated address is saved only for the API-key limiter; pod identity
  still uses the original socket address. The ingress must append or replace XFF with the
  actual client address. An extra invented entry on the left cannot select the bucket.
- **Proxy configuration.** Chart values `backend.forwardedHeaders.knownProxies` and
  `backend.forwardedHeaders.knownNetworks` are comma-separated IP addresses and CIDRs. Defaults
  are `127.0.0.1,::1` and `10.0.0.0/8,172.16.0.0/12,192.168.0.0/16`, respectively, covering
  common private ingress pod addresses. Every peer in those ranges can assert a client address:
  narrow them to your ingress source addresses or subnet. The chart's agent callback policy
  and dynamic browser egress policy permit backend ports 80/8080; these are layer-4 rules and
  permit `/api` as well as `/internal`. With the broad defaults those pods can assert XFF too.
  Use exact ingress IPs or a dedicated ingress source range to exclude untrusted workloads;
  namespaces do not generally have separate pod CIDRs. The defaults keep typical ingress
  installations working, rather than claiming to isolate workloads sharing a trusted range.
  Configure IPv6 ingress ranges explicitly. Set both strings to empty to ignore
  XFF entirely, including on direct installations. Outside the chart the default trusts loopback
  only. Invalid IPs/CIDRs fail startup rather than silently changing the trust boundary.
- **Deliberately no shared ingress bucket.** It would let one client lock out all other clients
  through the same proxy, including an owner presenting the right key. Each validated client
  gets its own bucket. A correct key from an unaffected client remains accepted.
- Rejected: parsing raw XFF, or clearing the middleware trust lists to accept every proxy.
  Either lets a direct caller rotate invented addresses to evade the limiter. Globally replacing
  `RemoteIpAddress` was also rejected because pod authorization needs the direct socket peer.
- **What counts.** A failure counts only where an endpoint challenges the API-key scheme. Personal
  tokens on `/api/remote` and OAuth tokens on `/mcp` also arrive as `Bearer` and pass through
  the same handler. Counting them would lock their owner out after ten legitimate calls.
- **Logs.** Each wrong key is a warning with the client and path, never the value. A throttled
  client's requests are not compared, so they add nothing to the log.
- Rejected: ASP.NET's rate-limiting middleware on 401 responses. It partitions before the
  request runs, so it cannot count only failures without a second pass, and it would also
  throttle the anonymous endpoints the key does not guard.
- Per replica: with `backend.replicas: 2` a client gets twice the attempts. That makes no
  difference to the defence, and a shared counter in Postgres would put a write on every failed
  request.

## One owner, always an admin

A key carries no identity, so it acts as one configured owner. `AdminAccess` adds that owner to
the `Ee:Admins` set itself. Leaving it to the values would lock the owner out of the admin area on
an install that lists somebody else there. On an install that lists nobody, it would rely on
bootstrap mode, which ends as soon as anyone gets a role in the UI.

Per-user keys were turned down. Several people on one install is what OIDC is for. Personal API
tokens (Settings → API tokens) already give scripts their own revocable credential, and they keep
working under the API key, issued to its owner.

## The remote MCP login

With `mcp.enabled` and no OIDC, `/connect/authorize` used to issue a token for `dev` to whoever
opened it. Registration is anonymous, so anyone could register a client and approve it on the
consent screen. In API-key mode the consent form now asks for the key, compared the same way, and
asks every time. A stored per-client approval cannot stand in for the key as it does under OIDC:
with no browser session to tie it to, it would hand the owner's token to any client that was ever
approved. Reconnects stay silent anyway, because they use the refresh token. The insecure mode
keeps the old behaviour.

## The chart

- `auth.apiKey.existingSecret` empty (the default): the chart generates 48 random alphanumerics
  into the Secret `agenthub-api-key` (keys `api-key`, `owner`) on first install. It reads them
  back with `lookup` on every upgrade, so neither changes. The backend gets the key through a
  `secretKeyRef`. It is not in the ConfigMap, where anyone allowed to read config could read it,
  and not copied into `agenthub-secrets`, so an existing Secret's rotation reaches the pod.
- `helm template`-based GitOps cannot `lookup`, so every render would roll a new key. Those
  installs set `auth.apiKey.existingSecret`. `NOTES.txt` says so.
- `--reuse-values` from a release older than these values carries no `auth` block at all. The
  helpers read it with `dig`; a plain `.Values.auth.apiKey` failed that upgrade with a nil
  pointer, which `tests/helm/api-key-upgrade.ps1` caught against a real API server.

## Upgrading an install that ran without OIDC

Its sessions, credentials and projects belong to `dev`. A key that acted as `admin` would hide
all of them, and that looks exactly like data loss. So the owner is chosen as follows:

1. `auth.apiKey.owner` if set;
2. otherwise the `owner` stored in the generated Secret;
3. otherwise `Auth__ApiKeyOwner` in the existing ConfigMap, which covers a Secret deleted to
   rotate the key and installs using `existingSecret`;
4. otherwise `dev` on an upgrade (the release existed before the key did) and `admin` on a fresh
   install.

Moving the data from `dev` to another name was rejected. It would rewrite owner columns in many
tables during a chart upgrade, and a partial run would split one person's data across two names.
Keeping `dev` costs nothing and can be changed deliberately later.

Rejected alternative: keep the open mode on upgrade and only warn. That would leave exactly the
installs that are open today open, which is the defect.

## Proof

`tests/ApiKeyAuthProof/` runs the real backend in each mode against real Postgres, object storage
and a rootless kube-apiserver. It drives the UI in real Chromium.
`tests/helm/api-key-upgrade.ps1` runs real `helm install`/`upgrade`, including from the v1.4.0
chart.
