# MCP policy proof (#514)

The session's MCP restrictions, set by the owner while the session runs, against the real Claude
Code in the session's pod:

| Piece | What runs |
|---|---|
| Backend | the real `AgentHub.Api` (insecure dev auth) on 127.0.0.1:8080, `backend.mjs` |
| Licence | an Enterprise token signed with a local test key; the backend is a throwaway build whose `EnterpriseLicense.PublicKeyPem` is that key's public half (never committed) |
| Kubernetes | a real kube-apiserver with the chart's RBAC; the backend uses an `agenthub-backend` token |
| Database | real Postgres on 127.0.0.1:54329 |
| Agent pod | `pod.mjs`: the env of the pod spec the backend wrote, the branch's `common/server.js` with the real Claude driver and the real hook settings (`mcp-policy-hook.sh`, `pretooluse-hook.sh`), and the pinned Claude Code in its PTY |
| MCP servers | `docs-mcp.mjs` as `docs`, `team-docs` and `ops.tools`: stdio servers with three tools and one resource that log every request |
| Model | a mock in `pod.mjs`: looks the MCP tools up with ToolSearch, as Claude Code defers them, calls each planned tool once, then reads a resource with `ReadMcpResourceTool` from `ops.tools` and from `docs` |

`proof.mjs` creates an interactive session and one with auto-approve. On each it blocks the tools
`mcp__docs__search` and `mcp__docs__get-data.v2` and the servers `team-docs` and `ops.tools`, typed
as an owner would. The model then calls those tools and `mcp__docs__read`, and reads a resource from each of `ops.tools` and `docs`. In the interactive
session it answers each approval request as the owner, with Allow. At the end it pauses and resumes
the first session and reads the rebuilt pod's `AGENTHUB_MCP_POLICY`.

Set up the rig as in `tests/SessionResourcesProof/README.md` (API server, RBAC, Postgres; no
object storage). The backend also needs `DataProtection__CertificatePem`/`__PrivateKeyPem` (any
self-signed pair). Then:

```sh
openssl ecparam -name prime256v1 -genkey -noout -out priv.pem; openssl ec -in priv.pem -pubout -out pub.pem
# throwaway worktree: put pub.pem into backend/Licensing/EnterpriseLicense.cs, dotnet build
PGDB=agenthub514 node backend.mjs <throwaway>/backend/bin/Debug/net10.0/AgentHub.Api.dll &
LICENSE_KEY=priv.pem REPO=<throwaway> KUBECTL=kubectl KUBECONFIG_ADMIN=<rig>/k8s/kubeconfig \
  CLAUDE_BIN_DIR=<dir with claude 2.1.318> node proof.mjs <scratch dir>
```

Facts found with Claude Code 2.1.318: MCP tools are deferred (not in `tools` until ToolSearch
returns them), and the tool names it reports replace every character outside `[A-Za-z0-9_-]`
with `_`, so server `ops.tools` becomes `mcp__ops_tools__…` and tool `get-data.v2` becomes
`get-data_v2`. The resource tools (`ListMcpResourcesTool`, `ReadMcpResourceTool`,
`ReadMcpResourceDirTool`) are deferred as well. Their hook input names the server
(`{"server": "...", "uri": "..."}`). Hook matchers also fire on the tools' aliases
(`ReadMcpResource`): an exact-name exclusion in the approval matcher filed an approval request for
every resource read, so both matchers use prefixes.

## What it does not cover

The pod runs outside a container, so the read-only root filesystem is not enforced. The
backend-unreachable case is covered by `McpPolicyHookContractTests` (real hook, real
controller), not by a CLI run here.
