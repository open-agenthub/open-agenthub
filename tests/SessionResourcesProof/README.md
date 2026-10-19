# Session resource limits proof (#215)

End-to-end proof for per-session CPU/memory limits and for restarting a running session to apply
them (docs/session-resources.md). Everything on the path is the real thing except the kubelet:

| Piece | What runs |
|---|---|
| Backend | the real `AgentHub.Api` (dev auth, no OIDC) on 127.0.0.1:8080 — `backend.mjs` starts it |
| Kubernetes | a real kube-apiserver + etcd + kube-controller-manager, with the chart's RBAC applied; the backend uses a TokenRequest token of `agenthub-backend` |
| Database | real Postgres |
| State storage | real S3-compatible object storage |
| Kubelet | `kubelet.mjs`: marks each session pod Running and uploads a state archive with real `tar` and `curl -T` to the pod's own presigned `AGENTHUB_STATE_PUT_URL`, as the runtime does |
| Browser | real Chromium (Playwright headless shell) against the branch's Vite app |

The rootless pieces come from the skills `rootless-kube-apiserver-admission-proof`,
`rootless-postgres-backend-tests`, `rootless-s3-session-deletion-proof` and
`rootless-chromium-dotnet-proof`. With `RIG` as the scratch directory:

1. API server: start the rig into `$RIG/k8s`, render the chart with `helm template`, apply its
   RBAC, ServiceAccount, Namespace and admission objects, and copy a kubeconfig carrying a
   `kubectl create token agenthub-backend` token to `~/.kube/config` (the backend reads that file).
2. Postgres on 127.0.0.1:54329 with a database `agenthub215`; object storage on 127.0.0.1:3910
   with a bucket `agenthub-proof` and its key in `$RIG/s3/key.txt` (never printed).
3. `node backend.mjs <backend>/bin/.../AgentHub.Api.dll` — ceilings 8 CPU / 16Gi.
4. `node kubelet.mjs &`
5. `node proof.mjs $RIG/k8s/kubeconfig <repo>` (admin kubeconfig: LimitRange, ResourceQuota).
6. `cd frontend && npx vite --port 5173`, then `node browser.mjs <chrome-headless-shell> <outdir> $RIG/k8s/kubeconfig`.

`proof.mjs` and `browser.mjs` resolve `pg` and `playwright-core` from their own directory
(`npm i pg playwright-core` next to them), and the MCP SDK from `agent-runtime/session-agent`.

What it cannot show: a kubelet's graceful termination and the runtime's own upload on SIGTERM
(pods without a node are deleted at once), scheduling against real node capacity, and the
cgroup actually enforcing a limit.
