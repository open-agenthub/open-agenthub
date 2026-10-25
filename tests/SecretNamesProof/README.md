# Duplicate secret names proof (#253)

Against the real backend (dev auth) acting as the chart's `agenthub-backend` service account on a
rootless kube-apiserver with the chart applied, real Postgres, the real stdio MCP server
(`mcp/agenthub`, personal API token), the real in-pod `agenthub_sessions` MCP server (a session's
callback token) and real Chromium on the branch's Vite app. Rig setup as in
`tests/SessionResourcesProof/README.md`; object storage is not needed.

1. `reset.sh <admin-kubeconfig> <postgres-bin>` between runs (stops the backend, drops the
   database, deletes what the backend wrote), then start the backend.
2. `node proof.mjs <admin-kubeconfig> <repo> <backend-log>` — `pg` resolved next to the script,
   the MCP SDK from `mcp/agenthub` (`npm ci` there and in `agent-runtime/session-agent`).
3. After another reset: `node browser.mjs <chrome-headless-shell> <outdir>` with
   `npx vite --port 5173` running in `frontend/`.

What it cannot show: a kubelet projecting the copies into a running container (no kubelet; the
copies are read from the API server), and a clash produced by two concurrent edits, which delivery
withholds (covered by `SessionSecretConflictTests`).
