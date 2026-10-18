# Sessions on a self-hosted model endpoint

A session used to reach its model one of two ways: a provider subscription login, or a provider API
key. Teams that run their own model server — llama.cpp's `llama-server`, vLLM, a gateway in front of
either — could not point a session at it, although every agent CLI supports a custom endpoint in
some form. This file records how that was added (#157), what each agent needs, and the alternatives
that were rejected.

## A third authentication mode, not a new agent

`AgentAuthMode.CustomEndpoint` sits next to `Subscription` and `ApiKey`. A session in that mode
carries two fields, `ModelEndpointUrl` (the API base, e.g. `https://llm.example.com/v1`) and
`ModelName` (the id the server serves), on `CreateSessionRequest`, `UpdateSessionRequest`,
`DuplicateSessionRequest` (copied while the mode stays) and `SessionInfo`. Both are stored on the
session row (`model_endpoint_url`, `model_name`).

The alternative was a new `AgentKind` ("Local"). It was rejected because the harness does not
change: a Claude Code session on a local model is still Claude Code, with its hooks, policy,
transcript, chat relay and resume. A new kind would have had to copy every one of those paths, or
pick one CLI and hide it behind a name that says nothing about which.

The endpoint's key lives under `custom_endpoint_api_key` in the owner's existing `creds-<owner>`
secret, written through the same write-only credential API as every other key
(`customEndpointApiKey`). One key per owner serves all of their endpoints. A per-session key was
considered and rejected for now: it needs a second write-only store with its own lifecycle, and the
common case — one team server — does not need it. The key is optional, because a server inside the
cluster often runs without one; a server that wants one refuses the runtime's start probe, and the
probe says so.

The URL must be absolute `http(s)`, without user info (a key there would be shown to everyone who
can read the session) and without query or fragment. The backend never fetches it — only the pod
does — so the field is not an SSRF surface in the backend.

Several places used to read "not `ApiKey`" as "runs on a provider account". `CustomEndpoint` has no
account either, so they now ask `AgentConfiguration.UsesProviderAccount`: otherwise a usage-limit
report from an endpoint session could retire a login the session never used, and a copy could carry
over a pinned account that creation then refuses. A personal API token that may not use stored API
keys may not start an endpoint session either — it runs on a stored key.

## The pod contract

The backend gives the pod provider-neutral variables and no provider credential:

| Variable | Value |
|---|---|
| `AGENTHUB_AUTH_MODE` | `customendpoint` |
| `AGENTHUB_MODEL_ENDPOINT_URL` | the stored URL, trailing `/` removed |
| `AGENTHUB_MODEL_NAME` | the stored model id |
| `AGENTHUB_MODEL_API_KEY` | `secretKeyRef` to `custom_endpoint_api_key`, optional |

No `ANTHROPIC_API_KEY`, `CODEX_API_KEY` or `OPENCODE_API_KEY`, and no login volume. Each runtime
maps the neutral names onto what its CLI reads, because the three CLIs disagree on wire protocol and
configuration. The alternative — the backend writing `ANTHROPIC_BASE_URL` and friends itself, as it
does for the provider keys — was rejected because Codex and OpenCode take no environment for this
at all; they need a config file, which only the runtime writes.

## Per agent: what each CLI needs

Verified against the pinned CLIs and a real `llama-server`, which serves all three protocols:

| Agent | Wire protocol | Configuration |
|---|---|---|
| Claude Code | Anthropic Messages, `POST /v1/messages` | `ANTHROPIC_BASE_URL` (the URL without `/v1`), `ANTHROPIC_AUTH_TOKEN`, `ANTHROPIC_MODEL` |
| Codex | OpenAI Responses, `POST /v1/responses` | a `[model_providers.<id>]` table in `config.toml` with `wire_api = "responses"` and `env_key`, plus `model` / `model_provider` |
| OpenCode | OpenAI Chat Completions, `POST /v1/chat/completions` | a `provider` block with `npm: "@ai-sdk/openai-compatible"` and `options.baseURL`; bundled with the CLI, nothing is downloaded |
| Cursor | — | refused at create: the CLI talks only to Cursor's own backend |
| OpenClaw | — | refused at create for now: its provider config is not written by anything here |

Two of these are easy to get wrong:

- **Claude Code does not speak OpenAI.** A server that only offers `/v1/chat/completions` cannot
  serve a Claude session. llama.cpp, recent vLLM and most gateways offer `/v1/messages`; plain
  OpenAI-only servers do not.
- **Codex no longer speaks Chat Completions.** The pinned Codex refuses `wire_api = "chat"` when it
  loads its config ("no longer supported"), so a Codex session needs a server with the Responses
  API. `llama-server` has it; many others do not yet.

A server that does not speak what the agent needs must fail the start with a reason, not leave a
session spinning: the runtime probes the endpoint before it starts the CLI and ends the container
with a termination message the session list shows (docs/session-startup-failures.md).

## Network: internet endpoints work, in-cluster ones are the operator's call

The session namespace is default-deny; `allow-agent-egress` opens 53, 80, 443 and 22 to anywhere. An
endpoint on the internet behind 443 therefore works as is.

An endpoint inside the cluster usually does not, even on port 80. NetworkPolicy matches the port of
the destination *pod*, after the Service has translated it, so a Service on 80 in front of a
container on 8080 is blocked although the URL says 80. Three ways to open it were weighed:

1. **The backend opens a per-session egress policy for the URL it is given.** Rejected. It would
   need cluster-wide read access to Services and EndpointSlices to find the real target port, and it
   would let any user punch through the default-deny by typing an internal URL — a session could
   then reach whatever internal service the user names, which is exactly what the default-deny is
   there to prevent.
2. **The agent asks for the port with `port_request`** (docs/network-ports.md). Rejected as the
   mechanism: the agent cannot ask anything before it has a model, and the grant opens the port to
   every destination, not to the model server.
3. **The operator declares the in-cluster model servers once, in the chart.** Chosen. A static
   policy allows agent pods to reach exactly those peers on exactly those ports. The operator already
   decides what sessions may reach; a model server is one more such decision, made once instead of
   per session. The chart value is `agent.modelEndpoints`:

   ```yaml
   agent:
     modelEndpoints:
       - namespace: llm
         podLabels: { app: llama-server }
         ports: [8080]   # the container port, not the Service's 80
   ```

   It renders one policy, `allow-agent-model-endpoints`. In it the namespace and pod selectors form
   one peer, so the port opens only on those pods and not on the whole namespace.

When the path is blocked, the start probe fails with a connection error and says that the egress
policy is the likely cause, instead of the CLI retrying until a timeout.

## Latency: the first turn takes minutes, and the CLIs must wait for it

A self-hosted server on one GPU processes the whole prompt before it sends the first byte. The first
turn of a session carries the CLI's system prompt and tool list. Against `llama-server` with a 30B
model it took 4 min 32 s for Claude Code, about 4 minutes for OpenCode and about 1 minute for Codex
`exec`. Later turns hit the server's prompt cache and answer in seconds. The test used a one-word
prompt, so what took that long was the prompt processing, not the generation.

The CLIs' own defaults are tuned for a provider's API, and two of them are shorter than that wait:

- OpenCode waits 5 minutes for response headers (`headerTimeout`).
- Claude Code has `API_TIMEOUT_MS` (600 s) and a separate first-byte window.
- Codex documents a 5-minute stream idle timeout.

A CLI that gives up disconnects, the server drops the half-processed prompt, and the retry starts
the same wait again. The session would never get an answer. The runtime therefore raises all three
to 30 minutes: `headerTimeout` in the OpenCode provider block, `stream_idle_timeout_ms` in the Codex
provider table, and `API_TIMEOUT_MS` / `CLAUDE_STREAM_FIRST_BYTE_TIMEOUT_MS` for Claude Code.

Nothing on the hub side is shorter. The pod's readiness probe is a TCP check on the session agent,
which listens before the CLI sends its first request. AgentHub sets no deadline on an autonomous
run. The chat relay reacts only to hook events, which come after the turn. The limit detector
matches the CLI's own limit messages, not silence.
