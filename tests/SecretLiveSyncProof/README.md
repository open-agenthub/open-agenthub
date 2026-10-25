# Secret changes reaching a running session (#255)

Needs a real kubelet, which a session pod does not have: k3s v1.34.1 in a rootless QEMU guest
(skill `agenthub-local-kubernetes-video-rig`, steps 1–3: bootstrap, start, install the cluster;
6 vCPUs / 6 GiB were enough). Then:

1. Apply the chart's objects into k3s (`helm template … --set postgres.password=x`, `kubectl apply`
   once without and once with `-n agenthub`, scale the chart's own workloads to zero), and write a
   kubeconfig with a `kubectl -n agenthub create token agenthub-backend` token to `~/.kube/config`.
2. Postgres on 127.0.0.1:54329 with a database `agenthub255` (skill `rootless-postgres-backend-tests`).
3. Start the branch's backend (dev auth) with `AgentHub__ClaudeAgentImage=docker.io/library/memcached:1.6-alpine`:
   it runs as uid 1000 on a read-only root filesystem and has a shell for `kubectl exec`.
4. `node proof.mjs <k3s-admin-kubeconfig>` with `pg` resolvable next to it.

`tests/SecretNamesProof/reset.sh` resets the database and what the backend wrote between runs.
