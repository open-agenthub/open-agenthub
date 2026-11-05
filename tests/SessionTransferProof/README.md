# Continue a session locally — proof (#453)

End-to-end proof that a Claude Code session can be taken off the cluster and handed back through
the stdio MCP server and through the single-use links the remote `/mcp` and the web app hand out
(docs/session-transfer.md). Everything on the path is the real thing except the kubelet and the
model:

| Piece | What runs |
|---|---|
| Backend | the real `AgentHub.Api` (dev auth) on 127.0.0.1:8080 — `backend.mjs` |
| Kubernetes | a real kube-apiserver + etcd + kube-controller-manager (admin kubeconfig in `~/.kube/config`) |
| Database / storage | real Postgres; real S3-compatible object storage with bucket `agenthub-proof` |
| Session pod | `pod.mjs`: restores state with the entrypoint's `curl` + `tar xzf` line, runs the **real pinned Claude Code** (`claude -p`, `--resume` after a restore) in the pod's `AGENTHUB_WORKDIR`, and persists with the runtime's own `state-archive.js` script to the pod's presigned URL |
| Model | `mock-model.mjs`: a Messages API that records every turn, so the proof reads which earlier turns a resumed CLI replayed |
| Laptop | a separate `HOME`; the real stdio server (`mcp/agenthub/server.mjs`) under a real MCP SDK client, and the real `claude --resume` |

Setup (skills `rootless-kube-apiserver-admission-proof`, `rootless-postgres-backend-tests`,
`rootless-s3-session-deletion-proof`), with `RIG=~/rig`:

1. API server rig into `$RIG/k8s`; `kubectl create ns agenthub-sessions`, label it
   `pod-security.kubernetes.io/enforce=baseline`, create the `agenthub-agent` ServiceAccount, copy
   the admin kubeconfig to `~/.kube/config`.
2. Postgres on 127.0.0.1:54329 with a database `agenthub453`; object storage on 127.0.0.1:3910 with
   its key in `$RIG/s3/key.txt` (never printed).
3. `dotnet build backend -c Release`, then
   `node mock-model.mjs $RIG/model &`, `node backend.mjs <repo>/backend/bin/Release/net10.0/AgentHub.Api.dll &`,
   `KUBECTL=~/k8s/kubectl node pod.mjs <repo> &`.
4. `cd mcp/agenthub && npm ci` (the proof takes the MCP SDK from there), then
   `KUBECTL=~/k8s/kubectl node proof.mjs <repo>`. Restart the mock model before a second run; the
   proof counts its recorded requests from one.

5. Optional, with the release tarball of PowerShell 7 unpacked: `node powershell.mjs <pwsh> <sessionId>`
   runs the PowerShell variants unmodified (curl.exe is a shim for curl on Linux).

What it checks: the unconfirmed call changes nothing; the confirmed one pauses and writes the
transcript under the local folder with every `cwd` rewritten and mode 0600; the laptop's real CLI
replays the pod's turns; a hand-back next to a live pod is refused and a second take-local does
not overwrite local turns; the hand-back is filed under the pod's folder again and the resumed
pod's real CLI replays the laptop turns; the web app's pasted bash commands work unmodified (a
`claude` shim turns the final interactive `claude --resume` into one `-p` turn) and a link works
once.

What it cannot show: a kubelet's graceful termination (pods without a node are deleted at once,
so the final upload on SIGTERM is not exercised), the remote `/mcp` over its OAuth transport (its
two tools call the same service and are covered by `SessionLocalTransferTests`), and Windows itself (the PowerShell commands run under real pwsh on Linux only).
