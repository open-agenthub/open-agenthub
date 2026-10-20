# Session grant control proof (#233)

Real Chromium drives the branch's Vite app against the real backend; every choice is read back
from the hub's API and from the `sessionsecrets-<id>` Kubernetes secret the pod's files come from.

| Piece | What runs |
|---|---|
| Backend | the real `AgentHub.Api` (dev auth) on 127.0.0.1:8080, `KUBERNETES_SERVICE_*` unset so it uses `~/.kube/config` |
| Kubernetes | a real kube-apiserver + etcd (skill `rootless-kube-apiserver-admission-proof`), namespace `agenthub-sessions` with the `agenthub-agent` ServiceAccount |
| Database | real Postgres on 127.0.0.1:54329 (skill `rootless-postgres-backend-tests`) |
| Kubelet | `tests/SessionResourcesProof/kubelet.mjs` without the state upload — the grant path never reads the archive, so no object storage is needed |
| Browser | Playwright's chrome-headless-shell with the libraries of skill `rootless-chromium-dotnet-proof` |

1. Seed through the API: secrets `PROJECT_KEY` (env), `deploy.json` (file), `DEPLOY_TOKEN` (env);
   a project selecting `PROJECT_KEY`; an Interactive session in it.
2. `cd frontend && npx vite --port 5173 --strictPort`
3. `node browser.mjs <chrome-headless-shell> <outdir> <sessionId> <kubectl> <kubeconfig>`
   (`npm i playwright-core` next to it). It resets the session's grants first, so it can be rerun.

Covered: the session view (arrow keys and pointer, each choice saved at once, file delivered to
and removed from the pod's secret, `project` and `granted` labels), the edit, new and duplicate
dialogs, and a 390px viewport without horizontal overflow.

Not covered: a kubelet syncing the secret volume into a running container, and environment
variables, which change only at the next start by design.
