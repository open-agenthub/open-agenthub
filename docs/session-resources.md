# Session CPU and memory

A session pod asks Kubernetes for the CPU and memory its owner set: `cpu` and `memory` on create,
update and duplicate, the token API, the MCP `session_create` tools and the "CPU request" and
"Memory request" fields of the session dialogs. That value becomes the pod's **request**: what the
scheduler reserves for it on a node.

The **limit** is what the container may use: above it CPU is throttled and the agent is killed for
memory. A session without one of its own gets the instance default, `agent.maxCpu` and
`agent.maxMemory`. Since #215 a session may set `cpuLimit` and `memoryLimit` itself, up to a ceiling
the operator sets.

## The problem (#162)

The value was only checked to be a Kubernetes quantity. `10m` is one: in memory it means ten
thousandths of a byte. It is almost always CPU notation typed into the memory field, most often by
an API or MCP caller. Such a pod reserves nothing on its node, so the scheduler overcommits the
node, and when the node runs short of memory the pod is among the first evicted. The session and its
workspace, an emptyDir, are lost.

The other direction failed too, only later. A request above the maximum is larger than the pod's
own limit, which the API server rejects when the pod is created. The session was stored and then
failed at its first start.

Both were reproduced with the real `KubernetesSessionService` against a real kube-apiserver and
Postgres:
- on main, `memory: "10m"`, `"1Mi"` and `cpu: "1m"` were accepted, and the pod requested exactly
  that;
- `cpu: "8"` and `memory: "8Gi"` were stored as sessions whose pods the API server refused.

## The problem (#215)

Every pod's limit was the instance maximum, 2 CPU and 4Gi by default. A session that needed more,
a large build or a test suite with a database, could only get it if the operator raised the limit
of every session, which raises what every pod may take from its node. A changed request also only
reached a running session at its next manual pause and resume, and the dialogs did not say so
where the value was typed.

Reproduced against the real backend on main: `cpuLimit: "6", memoryLimit: "12Gi"` was accepted and
silently dropped (the pod got `2`/`4Gi`), and `cpu: "3"` was refused as above the maximum, with no
way to raise it for one session.

## The rule

| | floor (chart) | default limit (chart) | ceiling for a session's own limit (chart) |
|---|---|---|---|
| CPU | `agent.minCpu`, default `50m` | `agent.maxCpu`, default `2` | `agent.cpuLimitCeiling`, empty = `agent.maxCpu` |
| Memory | `agent.minMemory`, default `256Mi` | `agent.maxMemory`, default `4Gi` | `agent.memoryLimitCeiling`, empty = `agent.maxMemory` |

- A limit lies between the floor and the ceiling. Empty or null means the default.
- A request lies between the floor and the limit the pod will get: the session's own, or the
  default. A pod whose request is above its limit is refused by the API server, so such a session is
  refused when it is stored instead.
- An edit that lowers the limit is checked against the request that stays, for the same reason.
- A ceiling below the default limit wins over it. It is the bound an operator set to keep pods
  under, and a default above it would break that bound for every session.

With both ceilings empty nothing changes for an existing installation: every session that sets no
limit gets the same pod as before, and a session's own limit can only be lower.

Create and update reject anything else with a 400 that names the bound, and whether the bound is
the instance's or the session's own limit (`Cpu request '3' is above this session's cpu limit of
2500m`). Memory written in milli also gets the reason, because that is the mistake behind every case
seen:

> Memory request '10m' is below the minimum of 256Mi. The suffix m means milli, so '10m' is a
> fraction of a byte; memory is given in Mi or Gi (e.g. '512Mi', '1Gi').

Every way to create or change a session goes through `KubernetesSessionService`, so the rule holds
everywhere: the web app, the remote API, child sessions spawned from a pod, the MCP tools, Git
webhooks and the chat relays. The backend's MCP `session_create` tool passes the message on as
`invalid_argument: …`. The stdio and in-pod MCP servers answer with their fixed error vocabulary
(`agenthub_http_400`, `sessions_backend_http_400`): they never pass backend text into the model's
context, so there the reason is only visible on the HTTP API.

The floor is a floor, not a recommendation. 256Mi is about what the agent CLI and the session
agent need to start; real work wants the default 1Gi.

## Sessions stored before the rule

Sessions created earlier may hold values outside the bounds, and so may sessions whose operator
later moves the bounds, a lowered ceiling included. They keep their record. When such a session
starts or resumes, its pod gets the nearest value inside the bounds; when the limit is clamped the
request follows it down. The backend logs a warning naming which resource was moved, not the
stored text, which is the owner's input.
Editing such a session works as long as the stored values are sent back unchanged, as the edit
dialog does with every save. A changed value is checked like a new one.

Rejected:

- **Rewriting the rows once (a migration).** It would change what the owner set without telling
  them. It would also need Kubernetes quantity parsing in SQL, or a startup job that touches every
  session.
- **Refusing to start such a session.** A session that ran yesterday would stop coming back after
  an upgrade, for a value its owner never saw a problem with.
- **Clamping the value when the session is read.** The dialog would show one value while the
  record holds another, and the next save would quietly persist it. `SessionInfo.resources` shows
  the clamped values separately, next to the stored ones.

## Applying a change to a running session

Requests and limits are part of the pod spec, so a running session only gets new ones in a new pod.
Saving them changes the record; the running pod is untouched, and `SessionInfo` says so with
`resourcesPendingRestart` while its pod still has the old values. The edit dialog and the Sessions
overview show the effective values and that note. The session header does not: its title block has
priority (docs/session-header-layout.md), and the values were clipped there at 1440 px.

`POST /api/sessions/{id}/apply-resources` restarts a running interactive session onto the stored
values. It is the edit dialog's "Restart now to apply the new resources", ticked by default when
the resources of such a session change; the dialog says that the session is paused and resumed,
that the conversation continues and that a turn in progress is interrupted. The dialog saves
first and restarts second, so a refused restart loses nothing: it says why, stays open, and the
values apply at the next start.

The restart is the confirmed pause and resume of #181 (docs/session-runtime-image-updates.md), held
under the same maintenance lock: pause (the runtime uploads its state on SIGTERM), wait for the old
pod to disappear so its last upload cannot race the new pod's download, check that the archive
exists, resume with `AGENTHUB_RESUME=1` and the same agent conversation id. It refuses an
autonomous session (resuming its prompt may repeat what it already did outside), a session that is
not running, and an instance without state storage; those get the values at their next start. A
session whose pod already has the values is left alone, so a repeated click restarts nothing.

Before pausing anything it asks the API server, in a dry run (`dryRun=All`), whether it would admit
the running pod's spec with the new resources, under a name of its own because the running pod
still holds the real one. A refusal is a 409 that quotes the API server and leaves the session
running. Without this, a LimitRange or quota the new values break would only show at the resume,
after the pause: the session would be stopped and fail to start. An admission webhook that does
not support dry runs fails every one, whatever it checks; that refusal is ignored and the restart
goes ahead unchecked, because refusing on it would make the restart impossible on such a cluster.

Rejected:

- **Resizing the pod in place** (the `pods/resize` subresource). It needs a cluster recent enough
  to have it, an RBAC grant the chart does not give, and whether a resize restarts the container
  depends on the container's resize policy and on what changes. A container restart outside the
  pause path skips the state upload the runtime makes on SIGTERM. The pause path works on every
  cluster the chart supports.
- **Restarting on every save.** An owner who changes a value while the agent is mid-turn would lose
  the turn without having been asked; the choice is in the dialog, with the cost next to it.
- **Draining first, as the automatic image update does.** Drain admission only exists for Claude in
  chat mode; every other session would need the confirmation anyway. The owner who ticks the box has
  already accepted the interruption, the same trade #181 made for its confirmed path.
- **Reading LimitRanges and quotas and checking them in the backend.** The chart's backend
  ServiceAccount cannot read them, and repeating the API server's admission logic would drift from
  it. The dry run asks the API server itself, with the permission to create pods the backend already
  has.

## ResourceQuota and LimitRange

The chart creates neither in the session namespace; an operator may. Both apply on top of the
rule above, and the API server enforces them, not the backend:

- **LimitRange** `max` per container below a session's limit: the pod is refused. At create or
  resume the session ends as Failed with the API server's message (docs/session-startup-failures.md);
  through apply-resources the dry run refuses it first and the session keeps running. Keep
  `agent.cpuLimitCeiling`/`agent.memoryLimitCeiling` at or below the LimitRange's `max`, or owners
  are offered values the cluster refuses. A LimitRange `default` does not change the agent container,
  which always sets both requests and limits; it does apply to the git-clone init container.
- **ResourceQuota** on `limits.cpu`/`limits.memory`: a session's limit counts in full against it,
  whatever it uses. Raising the ceiling therefore lets fewer sessions run at once in a namespace
  with such a quota; size the quota to the number of sessions times the limits they are expected to
  set. A quota on `requests.*` counts the requests the same way.
- During apply-resources the running pod still counts against the quota, so the dry run counts the
  old and the new pod together. Near a full quota it can refuse a change the restart itself would
  fit; the message says so, and pausing and resuming the session by hand then applies the saved
  values with only the new pod counted.

## Proof

`tests/SessionResourcesProof` (README.md there) runs the real backend with dev auth against a real
kube-apiserver with the chart's RBAC, the backend acting as its ServiceAccount, real Postgres and
real S3-compatible storage; only the kubelet is a stand-in that reports pods Running and uploads a
state archive through the pod's own presigned URL with real `tar` and `curl`. It showed:

- defaults unchanged (`500m/2`, `1Gi/4Gi`); own limits up to the ceiling admitted under Pod
  Security `baseline` (`3/6` CPU, `2Gi/12Gi`); out-of-bounds values refused with nothing stored;
- apply-resources replacing the pod with the new values, `AGENTHUB_RESUME=1` and the same
  conversation id, a second call restarting nothing;
- a LimitRange `max` of 10Gi refusing a 12Gi limit in the dry run while the session kept running
  on its pod, and a 14Gi `limits.memory` quota refusing 8Gi→10Gi with the running pod counted, then
  pause and resume fitting it;
- the limits in their own Postgres columns; the in-pod and stdio `session_create` passing them on;
- a ceiling lowered from 16Gi to 6Gi after the fact: record kept at `8Gi/10Gi`, pod started with
  `6Gi/6Gi`, the warning logged, an unchanged edit accepted and a new 9Gi refused;
- with the chart's empty ceilings, own limits above 2 / 4Gi refused and lower ones admitted;
- in real Chromium: the edit dialog's fields, defaults and effective values, Save and restart onto a
  new pod, the LimitRange refusal in the dialog, the "next start" choice and the Sessions overview's
  pending marker, and the new-session dialog at 390 px.

Not shown, because the rig has no kubelet: graceful termination and the runtime's own upload on
SIGTERM (pods without a node are deleted at once), scheduling against node capacity, and the
cgroup enforcing the limit.

## Where it lives

- `backend/Services/SessionResources.cs`: `ForCreate`, `ForUpdate`, `EffectivePair` and the
  `ResourceBounds` derived from the options.
- `KubernetesSessionService.ApplyResourcesAsync` and `AdmitResourcesAsync`: the restart and its dry
  run.
- The options are `AgentHub:MinCpu`, `MinMemory`, `MaxCpu`, `MaxMemory`, `CpuLimitCeiling` and
  `MemoryLimitCeiling`, set by the chart from `agent.*`.
- The helper containers a session may run (`docs/session-containers.md`) have bounds of their own,
  in `sessionContainers.container`.
