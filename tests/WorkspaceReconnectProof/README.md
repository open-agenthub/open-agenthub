# Workspace reconnect after a slow pod start (#520)

`proof.mjs` creates a session on the real backend (dev auth) against a real kube-apiserver and
plays the kubelet: it holds the pod Pending, marks it Running with an IP whose agent is not
serving yet, and after 40 s starts a stand-in agent there (WebSocket, `tty` subprotocol) and marks
the pod Ready. Chromium keeps the Workspace open throughout and records the status label, every
WebSocket the app constructs (wrapped constructor) and what reaches the network.

`ingress.mjs` stands in for the ingress in front of two backend replicas, one of them starved: it
balances WebSocket upgrades round-robin over the real backend and a replica that holds the request
without answering. Plain HTTP goes to the real backend.

## Run

Rig: `rootless-kube-apiserver-admission-proof` (namespace `agenthub-sessions` with the
`agenthub-agent` ServiceAccount, admin kubeconfig as `~/.kube/config`), Postgres as in
`rootless-postgres-backend-tests`, Chromium as in `rootless-chromium-dotnet-proof`.

```sh
# backend on :8081, dev auth, an unused agent port (never 7681: that is this pod's own agent)
ASPNETCORE_URLS=http://127.0.0.1:8081 Auth__InsecureDevMode=true AgentHub__AgentPort=7699 \
  AgentHub__Namespace=agenthub-sessions AgentHub__CallbackBaseUrl=http://127.0.0.1:8081 \
  DataProtection__CertificatePem=... DataProtection__PrivateKeyPem=... ConnectionStrings__Postgres=... \
  dotnet AgentHub.Api.dll
node ingress.mjs 8080 8081                       # Vite proxies /api and /ws to :8080
(cd ../../frontend && npx vite --port 5173)
ln -s ../../agent-runtime/session-agent/node_modules node_modules   # ws
KUBECONFIG=<admin> KUBECTL=<kubectl> CHROME=<chrome-headless-shell> PW_MODULE=<playwright-core> \
  OUT=<dir> STALL=connect node proof.mjs
```

`STALL=connect`: the agent port refuses connections while starting. `STALL=accept-only`: it takes
TCP connections and never answers the handshake.

## Results

| Frontend | Ingress | Result |
|---|---|---|
| main | one backend (no held replica) | recovers, 1.4 s (connect) / 2.0 s (accept-only) after ready |
| main | two replicas, one held | **stuck** "disconnected"/"connecting…" 60 s after ready, no terminal socket created after ready (2/2 runs) |
| branch | two replicas, one held | connected 2.0 s after ready (3/3, connect), 14.3 s (accept-only) |

Not shown: the real k3s ingress and a really starved replica (both stand-ins), and the real
session agent (a stand-in WebSocket server; the frontend only needs the socket to open).
