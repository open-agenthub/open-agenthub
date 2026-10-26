# Inline project browser proof

Start isolated Postgres on port 54329 and the rootless Kubernetes API rig described in the
`agenthub-real-controller-browser-proof` and `open-agenthub-real-backend-rig` skills. Create
namespace `agenthub-sessions` and ServiceAccount `agenthub-agent` in that rig.

Build `host.csproj`, then run its DLL with `PROOF_KUBECONFIG` set to the rig kubeconfig. It listens
on port 8080 with the actual project/session controllers, session service and Postgres stores.
Unrelated credential, catalog and browser providers return empty values; there is no kubelet.
Serve `frontend` with Vite on port 5173. Run `CDP_ENDPOINT=<browser CDP> PROOF_URL=<Vite URL>
node tests/InlineProjectProof/browser.cjs`; `PLAYWRIGHT_PATH` can point to a local playwright-core.

The proof creates a project from the dialog, gets a real 400 for an autonomous session without
a prompt, verifies the saved-project notice/selection, retries interactively, and verifies the
project id in the successful response. It then submits the same project name and verifies the
real 409. No routes are intercepted. Session startup is proven through admitted pod creation,
not execution of an agent or scheduling by a kubelet.
