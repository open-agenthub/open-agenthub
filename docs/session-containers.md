# Session containers

An agent often needs something running next to it to finish a task: a database for the
migration it writes, a queue, the service it is testing. A session pod is unprivileged and has
no Kubernetes API access, so before this it could only start such things as processes in its
own container, if the image had them at all.

Session containers let an agent start **helper containers over MCP**. Whatever a session starts
is never more privileged than the session itself, and the session's network policy applies to
it as well. Issue #107 holds the proposal; this file records what was built and which
alternatives were turned down.

## Shape

- **One namespace per session**, `<prefix><sessionId>` (default prefix `<sessionsNamespace>-`).
  It is created when the session starts its first helper and deleted on pause and on delete. It
  holds only what the session started, so cleanup is one namespace delete, the quota is one
  ResourceQuota, and nothing in it can name another session's pods.
- **The agent never talks to the Kubernetes API.** Four tools on the in-pod `agenthub_sessions`
  MCP server — `container_run`, `container_list`, `container_logs`, `container_stop` — call
  `/internal/sessions/{id}/containers` with the session's callback token, like every other
  internal route (the routes come first; the in-pod tools follow in a separate change). The backend builds the pod itself from a small request: image, command, args,
  env, ports, cpu, memory, emptyDir volumes, and whether the root filesystem is writable. Nothing
  else about the pod can be expressed.
- **One ClusterIP Service per helper that declares ports**, named like the helper. The session
  pod reaches it as `<name>.<namespace>.svc`, which `container_run` and `container_list` return
  as `host`. A helper without ports gets no Service.

## Privilege: enforced twice

1. **The hub builds the spec.** `SessionContainerSpecFactory.BuildPod` takes the pod and
   container security context from `AgentPodSpecFactory.PodSecurity` /
   `ContainerSecurity`. These are the same methods the session pod is built with, so the two
   cannot drift: uid 1000, `runAsNonRoot`, all capabilities dropped, no privilege escalation,
   RuntimeDefault seccomp, read-only root filesystem, no service-account token, no service
   links, the session's RuntimeClass. A session started with *Run as root* gets the root
   session's context, which is still unprivileged and still forbids escalation.
2. **The namespace refuses anything else.** It carries the Pod Security Admission label
   `enforce: restricted`, or `baseline` for a root session — never `privileged`. A bug in the
   hub that produced a privileged, hostPath or hostNetwork pod would be rejected by the API
   server.

The one relaxation an agent may ask for is `writableRootFilesystem`: many server images write
pid files or sockets next to their binaries. A writable root is the container's own overlay
layer, bounded by its ephemeral-storage limit, so it grants nothing the session lacks.

The PSA level is read off the namespace, not the session record. A session switched to root
takes effect at its next start, and a pause before that start deletes the namespace. Until
then, its helpers stay restricted.

## Who may create namespaces

Namespace create/delete is a cluster-scoped right, more than the backend held before. The chart
grants it in three parts (`templates/session-containers.yaml`):

1. **A ClusterRole bound cluster-wide** with `namespaces` get/list/watch/create/delete,
   `rolebindings` create, and `bind` on exactly one ClusterRole (by `resourceNames`). No
   update or patch on namespaces.
2. **A per-namespace ClusterRole** (pods, pods/log, services, resourcequotas, limitranges,
   networkpolicies — no secrets, no exec, no RBAC), which the backend binds to itself in each
   namespace it creates. Its pod rights therefore exist only where a session's helpers live.
3. **Two ValidatingAdmissionPolicies.** RBAC cannot select by label. On its own, (1) would let
   the backend create a RoleBinding — and with it pods — in any namespace. The policies match
   requests from the backend's service account only:
   - a namespace it creates or deletes must have the prefix, this release's
     `agenthub.dev/session-containers` label, and a PSA level of `restricted`/`baseline`
     (`restricted` alone when `agent.allowRootSessions` is off);
   - any namespaced write outside its two own namespaces must land in such a labelled
     namespace, and a RoleBinding there may grant only the per-namespace role, and only to the
     backend itself.

   The backend cannot label an existing namespace, since it has no update right on
   namespaces. So the only labelled namespaces are the ones it created. The label's value is
   the release namespace, so two hubs on one cluster cannot touch each other's namespaces.

This needs Kubernetes 1.30 or later (ValidatingAdmissionPolicy GA). `sessionContainers.enabled:
false` renders none of it.

**Rejected: one shared helpers namespace** with per-session labels and policies selecting on
them. It needs no new cluster rights, which is its whole appeal. But every helper of every
session would share one namespace, and isolation would rest entirely on label selectors the hub
writes correctly every time. A Service in that namespace selects across sessions unless its
selector is right. One quota would cover everyone, so a single session could exhaust it, or a
per-session limit would have to be counted by the hub itself. Cleanup would become a label-selected
delete over several kinds instead of one namespace delete. And a PSA level applies per namespace,
so root and non-root sessions' helpers would share `baseline`. The per-namespace design gets
all of these from the API server instead.

**Rejected: handing the agent a service account** with pod create in its namespace. Pod create
is the privilege that matters — hostPath, privileged, hostNetwork, image and service account are
all fields of a pod — and the agent could also create Services, Secrets, ConfigMaps and RBAC
objects unless every verb were pinned down. PSA alone stops the worst pods but none of the rest.

## Network

The helper namespace gets three policies, created before its first pod:

- `default-deny` — all ingress and egress;
- `allow-from-session` — ingress from the session's own agent pod (namespace *and* pod
  selector) and from the namespace's other helpers, so a service under test can reach its
  database;
- `allow-egress` — DNS, the namespace's own pods, and the session pod's TCP egress ports (22,
  80, 443, `agent.extraEgressPorts`, 3900 with the bundled object store). Like the session
  pod's `allow-agent-egress` there is no destination selector. Also like the session pod, a
  helper does not get the backend's port 8080: only `allow-agent-to-backend` opens it, and only
  for agent pods.

The sessions namespace is default-deny, so the session pod gets one more policy there,
`session-<id>-containers-out`, with egress to its helper namespace on any port. It carries
`agenthub.dev/session-containers-egress=true` and is removed with the namespace.

Runtime port grants (`docs/network-ports.md`) still apply to the session pod only. A helper
gets the session's static egress list, not the ports the owner approved for the session. A
helper is not reachable from the session browser either; the browser's preview pairs select the
agent pod.

## Limits and storage

- **ResourceQuota** per helper namespace from `sessionContainers.quota`: pods (default 5),
  cpu (2), memory (4Gi), ephemeral storage (10Gi). Services are capped at the pod count;
  NodePort and LoadBalancer Services and PersistentVolumeClaims at 0.
- **LimitRange** with the per-container maximum from `sessionContainers.container` (1 cpu, 2Gi).
  Every helper requests exactly what it limits, so the quota is a hard sum rather than
  something bursts exceed.
- **Per owner**, helpers are bounded by `maxRunningSessionsPerOwner` times the per-session
  quota. **Rejected for now: a separate per-owner cap.** It would need a count across namespaces
  on every run. The session limit already bounds it, and an operator who wants less lowers the
  quota.
- **Storage is emptyDir only**: `/tmp` always, plus at most four volumes the request names, each
  with a `sizeLimit` of at most `maxVolumeSize` (2Gi). That is also each helper's
  ephemeral-storage limit. **Rejected for now: PersistentVolumeClaims with a quota.** A helper
  does not survive a pause (below), so durable storage would outlive everything that uses it.
  Claims also bring storage classes, reclaim policies and orphaned volumes into what the hub
  has to clean up. The quota forbids claims outright, so adding them later is a deliberate change.

## Lifecycle

- **Pause** deletes the namespace. A paused session holding a database's quota would keep it
  from every session that runs, and emptyDir data is gone once the pod is anyway. After a
  resume the agent starts its helpers again.
- **Delete** deletes the namespace and the session's egress policy. Errors are logged, never
  thrown, so a session delete does not fail over its helpers.
- **The reconcile sweep** (every 5 minutes) deletes the namespaces of sessions that are gone,
  paused, succeeded or failed. This covers a backend restarted mid-delete and a session that
  ended on its own.
- **Scheduled sessions cannot start helpers.** A scheduled run is a job that ends, with nobody to
  stop what it started. The pod env withholds the tools and the route refuses as well.

## Images

`sessionContainers.allowedRegistries` lists the registry hosts a helper image may come from.
The host is resolved the way a container runtime does it: the first path segment is a host only
if it contains `.` or `:` or is `localhost`, otherwise the image is on `docker.io`. A prefix
comparison would let `docker.io.example.com/x` pass a `docker.io` list. An empty list allows
any registry, as a custom session image may use today. `requireDigest: true` accepts only
`image@sha256:…`. Helpers get no image pull secret: the instance's pull secret lives in the
sessions namespace and is not copied into namespaces the agent's requests populate.

## Where the tools live

**Chosen: four more tools on `agenthub_sessions`**, registered only when
`AGENTHUB_CONTAINERS_ENABLED=1`. The backend sets that flag only when the feature is on, the
spawn MCP is on, and the session is not scheduled.

**Rejected: a new `agenthub_containers` server.** Each in-pod server is wired separately into
five runtimes' MCP configuration (Claude's `.mcp.json`, Codex's TOML, Cursor's and OpenClaw's
JSON, OpenCode's managed config), each with its own tests. A new server would add a sixth
copy of that wiring for four tools. Its one advantage, a separate allow-list prefix, comes from
the tool names already (`mcp__agenthub_sessions__container_*`).

**Not provided: `container_exec`.** `pods/exec` on the hub's credentials is the most powerful
verb in the namespace, and the agent already reaches every helper over the network with its own
clients (`psql -h db.<namespace>.svc`). `container_logs` covers start-up failures.

**Not on the remote token API (yet).** The remote API acts for a signed-in user; helpers belong
to a running session's agent. Exposing them remotely needs a decision on which session a remote
caller's containers attach to, which is not part of #107.

## Verified, and what needs a real cluster

Against a real `kube-apiserver` 1.34 with etcd and `kube-controller-manager`, run rootless in a
session pod and called with a TokenRequest token of the chart's real `agenthub-backend` service
account — the RBAC and admission policies applied from `helm template` output:

- the hub's own helper pod is admitted under `restricted`; a root session's under `baseline`;
- PSA rejects a privileged pod (both levels, with `--allow-privileged=true` so PSA and not the
  API server's own switch decides), a root pod under `restricted`, and hostPath and hostNetwork
  pods under `baseline`;
- the namespace policy rejects a namespace without the label, with another release's label,
  outside the prefix, with PSA `privileged` or with no PSA label, and the deletion of an ordinary
  or an unlabelled prefixed namespace; RBAC rejects relabelling one;
- the scope policy rejects RoleBindings in `kube-system` and `default`, and RoleBindings granting
  the role to `system:anonymous` or to the agent's service account; RBAC's escalation check
  rejects binding `cluster-admin`; pods in `kube-system` and secrets anywhere are forbidden;
- the quota refuses a pod over its cpu sum, and the refusal reaches the agent as a sentence;
- cleanup deletes the namespace — the namespace controller removes pods, Service, quota and
  policies — and the session's egress policy in the sessions namespace.

That rig has no kubelet and no network plugin, so these still need a real cluster:

- **NetworkPolicy enforcement**: that the agent reaches `db.<ns>.svc:5432`, and that a helper
  cannot reach the backend, another session's helpers or the sessions namespace;
- that the default images (e.g. `postgres`, `redis`) actually start as uid 1000 with a
  read-only root, or need `writableRootFilesystem`;
- `container_logs` of a running container (no kubelet, so no logs in the rig);
- the chart on a cluster older than 1.30, where the ValidatingAdmissionPolicy kinds do not
  exist and the install fails, which is why the switch exists.
