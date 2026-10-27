# Secret request proof (#330)

An answered `secret_request` never reached a session that another session had spawned, and the
request stayed open. These scripts reproduce that and prove the fix against the real pieces:

| Piece | What runs |
|---|---|
| Backend | the real `AgentHub.Api` (insecure dev auth) on 127.0.0.1:8080, `backend.mjs` |
| Kubernetes | a real kube-apiserver with the chart's RBAC; the backend uses a `agenthub-backend` token |
| Database | real Postgres on 127.0.0.1:54329, database `agenthub330` |
| Agent pod | `pod.mjs`: the env of the pod spec the backend wrote, the branch's `common/server.js` with the real Claude driver, hook settings and in-pod MCP merge, and the pinned Claude Code 2.1.287 in its PTY |
| Kubelet | `pod.mjs` marks the pod Running on 127.0.0.1 and projects `sessionsecrets-<id>` into `$HOME/.secrets` as a secret volume does (`..data` link) |
| Model | a mock in `pod.mjs`: calls `secret_request`, reads the file with Bash, repeats what it read |
| Browser | real Chromium (Playwright headless shell) on the Vite app, `e2e.mjs` |

Set up the rig as in `tests/SessionResourcesProof/README.md`; no object storage is needed. Then:

```sh
npm i pg playwright-core            # next to these scripts
node backend.mjs <backend>/bin/Debug/net10.0/AgentHub.Api.dll &
(cd ../../frontend && npx vite --port 5173 --strictPort &)
node repro.mjs <repo>              # HTTP only: owner-created vs agent-spawned session
REPO=<repo> KUBECTL=kubectl KUBECONFIG_ADMIN=<rig>/k8s/kubeconfig CLAUDE_BIN_DIR=<dir with claude 2.1.287> \
  ANSWER_AFTER_MS=150000 node e2e.mjs <chrome-headless-shell> <outdir> <scratch dir>
```

The backend is started with `AgentHub:AgentPort=7699`, so the hub reaches the rig's agent and not
the session agent of the pod the proof runs in.

## Results

| | main (f168837) | branch |
|---|---|---|
| `repro.mjs`, owner-created session | 6/6 | 6/6 |
| `repro.mjs`, agent-spawned session | `provide` → 403 `secret_not_allowed` | 6/6 |
| `e2e.mjs` | 8/16: Save shows nothing, the agent's call never returns `provided`, the request is still there after a reload | 16/16, also with the owner answering after 150 s |

Claude Code 2.1.287 moves an MCP call still running after 120 s into the background and reports
its result later as a task notification. With `ANSWER_AFTER_MS=150000` the agent got `provided`
that way, read the file and continued.

## What it does not cover

A real kubelet's sync period (measured in #255: 60–85 s for a file) and a variable delivered
through a restart. The pod runs outside a container, so the read-only root filesystem is not
enforced.
