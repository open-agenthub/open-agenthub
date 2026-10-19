# Session CPU and memory requests

A session pod asks Kubernetes for the CPU and memory its owner set (`cpu`, `memory` on create and
update, the "CPU" and "Memory" fields of the session dialogs, the `cpu` and `memory` arguments of the
MCP `session_create` tool). That value becomes the pod's **request**: what the scheduler reserves for
it on a node. The **limit** is the same for every session, the instance maximum (`agent.maxCpu`,
`agent.maxMemory`).

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

## The rule

A request must lie between a floor and the instance maximum:

| | floor (chart) | maximum (chart) |
|---|---|---|
| CPU | `agent.minCpu`, default `50m` | `agent.maxCpu`, default `2` |
| Memory | `agent.minMemory`, default `256Mi` | `agent.maxMemory`, default `4Gi` |

Create and update reject anything else with a 400 that names the bound. Memory written in milli
also gets the reason, because that is the mistake behind every case seen:

> Memory request '10m' is below the minimum of 256Mi. The suffix m means milli, so '10m' is a
> fraction of a byte; memory is given in Mi or Gi (e.g. '512Mi', '1Gi').

Every way to create or change a session goes through `KubernetesSessionService`, so the rule holds
everywhere: the web app, the remote API, child sessions spawned from a pod, the MCP tools, Git
webhooks and the chat relays. The MCP `session_create` tool passes the message on as
`invalid_argument: …`. Before, an unhandled exception there turned into the SDK's generic error,
and an agent could not tell what to fix.

The floor is a floor, not a recommendation. 256Mi is about what the agent CLI and the session
agent need to start; real work wants the default 1Gi.

## Sessions stored before the rule

Sessions created earlier may hold values outside the bounds, and so may sessions whose operator
later moves the bounds. They keep their record. When such a session starts or resumes, its pod
asks for the nearest value inside the bounds, and the backend logs a warning naming which resource was moved,
not the stored text, which is the owner's input.
Editing such a session works as long as the stored value is sent back unchanged, as the edit dialog
does with every save. A changed value is checked like a new one.

Rejected:

- **Rewriting the rows once (a migration).** It would change what the owner set without telling
  them. It would also need Kubernetes quantity parsing in SQL, or a startup job that touches every
  session.
- **Refusing to start such a session.** A session that ran yesterday would stop coming back after
  an upgrade, for a value its owner never saw a problem with.
- **Clamping the value when the session is read.** The dialog would show one value while the
  record holds another, and the next save would quietly persist it.

## Where it lives

- `backend/Services/SessionResources.cs`: `Validate` (create, update) and `Effective` (start).
- The floor and maximum options are `AgentHub:MinCpu`, `AgentHub:MinMemory`, `AgentHub:MaxCpu`
  and `AgentHub:MaxMemory`, set by the chart from `agent.*`.
- The helper containers a session may run (`docs/session-containers.md`) have bounds of their own,
  in `sessionContainers.container`.
