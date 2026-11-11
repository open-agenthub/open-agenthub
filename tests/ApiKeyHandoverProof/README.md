# API-key handover proof (#501)

End-to-end proof for handing the data of the API-key owner to a team-login user
(docs/apikey-handover.md). One run takes an install through the pilot path:

1. **Development mode** (`Auth__InsecureDevMode`): `dev` stores an API key, a Git token, a secret, a
   project and two sessions; one is paused (state archive in object storage), one stays running.
2. **API-key mode** (`Auth__ApiKey`, owner `admin` as in the quickstart): `admin` stores credentials,
   a project `Tools`, an interactive session (paused, archive stored), a subscription session whose
   pod uploads a Claude login through the real internal route, and a scheduled session (CronJob).
3. **OIDC** with a local issuer: `root` (in `Ee:Admins`), `alice` and `bob` sign in. `alice` already
   has her own API key and a project `tools`.

Then `root` hands `admin`'s data to `alice` **with** credentials and `dev`'s data to `bob` **without**,
both in Chromium, and the script checks the result over HTTP, in the API server and in the bucket.

| Piece | What runs |
|---|---|
| Backend | the branch's real `AgentHub.Api.dll`, restarted in each mode on the same database |
| Identity | `identity.mjs`: a local OIDC fixture with real RS256 tokens, PKCE and single-use codes, reached through the Vite proxy at `/__identity` |
| Kubernetes | a real kube-apiserver + etcd + kube-controller-manager (job, cronjob controllers); the backend uses the admin kubeconfig (RBAC is not under test here) |
| Kubelet | `tests/SessionResourcesProof/kubelet.mjs`: marks session pods Running and uploads a real state archive to the pod's own presigned URL |
| Database | real Postgres, a fresh database per run |
| Object storage | real S3-compatible storage (Garage) |
| Browser | real Chromium (Playwright headless shell) against the branch's Vite app |

## Running it

The rootless pieces come from the skills `rootless-kube-apiserver-admission-proof` (add `job,cronjob`
to the controller list), `rootless-postgres-backend-tests`, `rootless-s3-session-deletion-proof` and
`rootless-chromium-dotnet-proof`. With `RIG` as the scratch directory:

1. API server in `$RIG/k8s` (`$RIG/k8s/kubeconfig`), Postgres on 127.0.0.1:54329, object storage on
   127.0.0.1:3910 with a bucket `agenthub-proof` and its key in `$RIG/s3/key.txt` (never printed).
2. `dotnet build backend -c Release`; `npm ci` in `frontend/`.
3. In a scratch directory: `npm i pg @aws-sdk/client-s3 playwright-core`, then from there
   `RIG=$RIG KUBECTL=<kubectl> node <repo>/tests/ApiKeyHandoverProof/proof.mjs <repo>/backend/bin/Release/net10.0/AgentHub.Api.dll <chrome-headless-shell> <outdir>`.

The script starts the issuer, Vite, the kubelet stand-in and the backend itself, writes their logs,
`results.txt` and four screenshots to `<outdir>`, and exits non-zero on any `FAIL`. Delete the
`agenthub-sessions` namespace between runs; the bucket may be reused (checks are per session).

## What it cannot show

- A real kubelet and the runtime's own state upload on SIGTERM (pods without a node go at once).
- What the agent CLI shows when a moved session starts without the credentials that stayed behind:
  the proof shows the pod is created without any reference to the old owner's secrets, not the
  CLI's sign-in screen.
- RBAC: the chart's role already grants get/list/create/update/delete on secrets and cronjobs,
  which is all the handover uses; the backend here runs as cluster admin.

## The card's wording alone (#574)

`intro-card.mjs` serves `HandoverCard.vue` on its own through the frontend's Vite, with the
`/api/admin/handover` answer stubbed inside the page, so a change to the card's text can be looked
at in any browser without the rig above: `node tests/ApiKeyHandoverProof/intro-card.mjs <repo> [port]`
(after `npm ci` in `frontend/`), then open `http://<host>:<port>/`. It shows only what the card
renders for a fixed overview; nothing about the backend.
