# Session organization proof

Use the `agenthub-real-controller-browser-proof` and `open-agenthub-real-backend-rig` skills to
start isolated Postgres on 54329 and the real rootless Kubernetes API. Create namespace
`agenthub-sessions` and ServiceAccount `agenthub-agent` in that rig. Build `host.csproj` and run
the DLL with `PROOF_KUBECONFIG` pointing at its kubeconfig. Start this branch's Vite app on 5173.

The host runs the actual session, project, secret and organization controllers, the session
service with a Postgres maintenance lock, and the actual session/project/order stores. Browser,
catalog, credential-provider and artifact services are empty dependencies. Authentication uses
synthetic users; `X-Proof-Owner` selects a user in this isolated host only.

Run these with `CDP_ENDPOINT` and `PROOF_URL` pointing at the session browser and Vite:

- `node tests/SessionOrganizationProof/browser.cjs`: real drag order, persistence after reload,
  project drop confirmation, foreign-session/order HTTP 404, keyboard reorder/move and touch
  reorder/move in a 390×844 context.
- `KUBECTL=<kubectl> PROOF_KUBECONFIG=<rig kubeconfig> node tests/SessionOrganizationProof/active.cjs`:
  creates real project secrets and an admitted pod; foreign project 404 and direct active move
  409; confirms Pause and move in Chromium; checks old pod removal, paused registry and new
  project; explicitly resumes and checks that the admitted pod references the new environment
  secret and does not reference the old one.

`PLAYWRIGHT_PATH` can override the local playwright-core module path. No routes are intercepted.
No kubelet runs: CLI execution, graceful archive upload and in-container secret consumption are
not established. The proof verifies admitted pod specifications and real controller/persistence
behavior. File access changes use the existing project resolver, covered by the backend suite;
these browser scripts verify the confirmation text rather than transfer actual file content.
