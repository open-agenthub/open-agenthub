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

## Scheduled sessions (#295)

Start the rig's kube-controller-manager with `--controllers=...,job,cronjob` so real runs start
on schedule. Seed a fake key only: `kubectl -n agenthub-sessions create secret generic
creds-<owner key> --from-literal=anthropic_api_key=sk-test-proof` (the owner key is `u-` plus
the first 16 hex characters of the SHA-256 of `proof-owner`). Then run:

- `KUBECTL=<kubectl> PROOF_KUBECONFIG=<rig kubeconfig> node tests/SessionOrganizationProof/scheduled.cjs`:
  waits for a real run in the source project, then checks a foreign project or owner gets 404,
  PATCH gets 409, and a move during the run gets `scheduled_run_active` with the schedule left
  as it was. It confirms Stop run and move by keyboard in Chromium. Then the run's Job and pod
  are gone, the template and secret copies hold only the destination, and the schedule is
  active. It waits for the next real scheduled run and resolves its pod's `secretKeyRef`s: the
  destination's value is present, and no source variable or value is. Finally it moves an idle
  schedule by touch at 390×844.

Without a kubelet, the proof verifies the admitted run pod and the secrets it references, not a
running container's environment. The failure paths (each step failing after the suspension)
are covered by `ScheduledSessionMoveFailureTests` against a fake cluster API.

## Scheduled templates following secret changes (#301)

Same rig and fake key as above. Run
`KUBECTL=<kubectl> PROOF_KUBECONFIG=<rig kubeconfig> node tests/SessionOrganizationProof/secret-sync.cjs`. It:

- waits for a real run, then changes the project's selection and renames a secret while that run
  is in progress. It grants a third secret through the session view's grant switch in Chromium.
  The next real run resolves exactly the renamed, the added and the granted variable, and the
  first run kept its pod.
- installs a ValidatingAdmissionPolicy that refuses CronJob updates, so the template patch
  really fails. The project change still returns 200, the session reports
  `scheduleSecretsOutdated`, and the sidebar marks it. Update schedule answers 409 with the
  reason. Once the policy is removed, Update schedule applies the change, and the next run
  resolves exactly the current selection.

The script deletes a run's Job to stand in for the run finishing: without a kubelet its pod stays
Pending, and the CronJob's Forbid policy would hold the next run back.

## Deleting a secret fails closed (#303)

Same rig and fake key. Run
`KUBECTL=<kubectl> PROOF_KUBECONFIG=<rig kubeconfig> node tests/SessionOrganizationProof/secret-delete.cjs`.
It uses a scheduled and an interactive session in one project:

- Phase A: a ValidatingAdmissionPolicy refuses CREATE and UPDATE of the session copies. The
  delete answers 204, and both sessions' copies are withdrawn. A real run started then
  references the deleted name optionally and resolves nothing. Once the policy is lifted, the
  next sync restores both copies without the value. For the interactive session that happens
  through its live pod.
- Phase B: the policy also refuses DELETE. The delete from Settings → Secrets in Chromium answers
  503 `secret_copies_pending`, and the notice with Retry shows. The copies still hold the value,
  which is why no success was reported. After the policy is lifted, Retry answers 204, the copies
  hold only the remaining secret, and the next run resolves only that one.
