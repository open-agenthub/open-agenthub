# Connected-provider token renewal proof (#289)

End-to-end proof that a running session keeps working GitLab credentials across token expiry,
that concurrent callers spend a rotating refresh token once, and that a refused grant becomes a
"sign in again" state (docs/git-credentials.md, "Renewing a connected provider's token").

| Piece | What runs |
|---|---|
| Backend | the real `AgentHub.Api` (dev auth) on 127.0.0.1:8080, the branch's or main's build — `backend.mjs` |
| Kubernetes | a real kube-apiserver + etcd (skill `rootless-kube-apiserver-admission-proof`); the gitauth and gitcreds secrets live there |
| Database | real Postgres on 127.0.0.1:54329 (skill `rootless-postgres-backend-tests`); the advisory lock is real |
| GitLab | `mock-gitlab.mjs` on https://127.0.0.10 — OAuth with expiring access tokens and single-use rotating refresh tokens, the API calls the hub makes, a private repository over real git smart HTTP (`git http-backend`), the failed-login ban |
| Pod | `kubelet.mjs` mounts `gitcreds-<id>` as `~/.git-credentials` like the entrypoint, starts the **real** `common/server.js` (`agent.cjs`) on the pod's own loopback address, and marks the pod Running |
| git | the real git client over HTTPS, `credential.helper store`, in the pod's home |

Setup, with `RIG` a scratch directory and the API server's admin kubeconfig copied to
`~/.kube/config` (namespace `agenthub-sessions` and ServiceAccount `agenthub-agent` created):

```bash
bash setup.sh $RIG/mock          # CA + certificate for 127.0.0.10, client secret, bare repository
export PG_BIN=<postgres bin dir> LD_LIBRARY_PATH=<postgres libs>
# expiry: one session, fetch + push every 10 s for 200 s with 60 s tokens, then revoke, then sign in again
SECONDS_ARG=200 bash run.sh $RIG <repo> <backend>/bin/Debug/net10.0/AgentHub.Api.dll agenthub289a expiry \
  '{"AgentHub__GitOAuthRefresh__Margin":"00:00:25","AgentHub__GitOAuthRefresh__Interval":"00:00:03"}' ACCESS_TTL=60
# concurrency: the token is due, background renewal off, 3 session starts + 6 project searches at once
WAIT_DUE=40 bash run.sh $RIG <repo> <dll> agenthub289b concurrency \
  '{"AgentHub__GitOAuthRefresh__Margin":"00:00:25","AgentHub__GitOAuthRefresh__Enabled":"false"}' ACCESS_TTL=60
```

Binding 443 needs `net.ipv4.ip_unprivileged_port_start` at 0 (it is in a session pod); the store
line has no port, so the mock must be on 443. No token is printed: outputs are scrubbed, and the
last check searches every log of the run for every token the rig stored.

Results (2026-10-26):

| | main | branch |
|---|---|---|
| expiry, 60 s tokens | fails from the first expiry on (`Authentication failed`, then `could not read Username` for good) | 20/20 fetch+push rounds across 5 renewals |
| revoked grant | still "connected" | `needsSignIn`, line removed from the pod's store |
| new sign-in | pod stays broken | same pod pushes again, no restart |
| 3 starts + 6 searches at once | 8 `invalid_grant`, 5 of 6 searches empty, all 3 sessions hold the revoked token | 1 renewal, 0 `invalid_grant`, all 9 calls get the new token |

Cannot show: a real GitLab's timing of revoking the previous access token, the real ban
thresholds, and kubelet secret-volume propagation (the push route makes that irrelevant for a
running container; the secret only matters for a container restart).
