# Account-aware model discovery (#461)

The new-session dialog offered one model field, and only for a custom endpoint, where the person
typed an id from memory. Every other session started on whatever its CLI picks by default; a
different model meant opening the CLI's own `/model` menu after start, and nothing in the web app
said which models or controls (reasoning effort, context size, image input) the selected
**account** offers. The adoption evaluation (`t3code-adoption.md`, follow-up 1) proposed
discovery with short-lived per-account caches, stale/unknown states and a manual model-ID escape
hatch. This file records the sources, where discovery runs, and what was rejected.

## Sources per provider, as the pinned CLIs behave

Each source was run against the pinned CLI, with a recording mock standing in for the provider's
service (`tests/ModelDiscoveryProof/`). "Account-aware" means the answer depends on the
credential the session uses, not only on the CLI version.

| Agent | Source | Account-aware? | Observed |
|---|---|---|---|
| Claude Code 2.1.318 | SDK control request `initialize` on `claude -p --input-format stream-json --output-format stream-json`; the answer's `models[]` | By plan: a team subscription login added a 1M-context variant that a logged-out or API-key CLI does not list | Per model: id, resolved model, label, description, effort levels, adaptive thinking, fast mode. No request to the model service, so an API key's entitlement is not checked |
| Codex 0.167.0 | `codex app-server` JSON-RPC `model/list` | Subscription login: yes. The CLI fetches `<backend>/codex/models` with the account's token and account id; two accounts got two different lists | A refusal (403) is **not** an error in `model/list`: the CLI logs `failed to refresh available models: unexpected status 403` and answers with its bundled list. API key: bundled list, no request at all |
| Cursor (unpinned) | `agent models` ("List available models for this account") | Yes | Needs a valid login; no login ends with `Authentication required`, an invalid key with `The provided API key is invalid`. The success format (`<id> - <name> (current, default)` under `Available models`) is read from the CLI's bundle, **not verified with a real account** |
| OpenCode 1.18.35 | `opencode models --verbose` | Only by which providers are logged in | Catalog from the CLI's models database, with reasoning, attachment, tool-call and context limits. Not an entitlement check |
| OpenClaw | — | — | Not offered: manual id only (see Limits) |
| Custom endpoint (Claude, Codex, OpenCode) | `GET <endpoint>/models` from the pod, with the endpoint key | Yes, it is the server's own list | Codex lists its bundled models even when configured for an endpoint, so its CLI answer would be wrong there |

Two consequences shape the design:

- **A list is not an entitlement.** Claude with an API key, Codex with an API key and OpenCode
  report what the CLI knows, not what the key may use. The catalog therefore carries its
  `source` and says "not verified for this account" where that is the case, instead of
  presenting every row as available.
- **A silent fallback must be surfaced.** Codex answers a refused account with its bundled list.
  The probe reads the CLI's refresh error and marks the result `fallback` with the reason, so a
  person does not pick from a list the account was just refused.

## Where discovery runs: in the session pod, with the session's credential

The probe runs in the session pod, as a second short-lived CLI process beside the interactive one,
with the same HOME, environment and login the agent uses (`common/model-catalog.js`, route
`GET /agenthub/models`). The interactive CLI is not touched.

The probe must not behave as a second agent. Claude's runs with `--setting-sources ''`,
`--no-session-persistence`, `--strict-mcp-config` and without `CLAUDE_CODE_PLUGIN_DIRS`: with the
session's settings it fired the SessionStart and SessionEnd hooks (which report to the hub), and
the fleet mod would compete for the session's inbox. A negative control in the proof shows both
hooks running without these flags. `--bare` was rejected: it also stops reading the OAuth login,
which is what makes the list account-aware. The result is normalised to a fixed
set of fields and bounded (200 models, 2 KiB of text per model) before it leaves the pod.

Rejected alternatives:

1. **The backend asks each provider's model service itself.** It would need to hold and present
   subscription tokens that today only the pod uses, keep a client per provider in step with what
   the pinned CLI accepts, and fetch user-supplied endpoint URLs — the backend never fetches a
   custom endpoint (`custom-model-endpoints.md`), and this would make the URL field an SSRF
   surface.
2. **A throwaway probe pod per dialog.** A pod start per dialog open (image pull, credential
   mount, scheduling) to fill a list the person may not need, and a second place where provider
   credentials are mounted. Rejected for now; the cache below covers the dialog.
3. **Scraping the CLI's own `/model` menu from the running TUI.** It edits the person's input box
   and differs per CLI version, the reason slash commands are not scraped either
   (`slash-commands.md`).

## Caching, states and the manual fallback

- **In the pod**, a result is kept 10 minutes, keyed by a hash of the credential material the CLI
  reads (login file contents, key variables, endpoint URL). An account switch rewrites that
  material, so the next request misses. One probe runs at a time; concurrent requests share it.
  The hash is never logged or returned.
- **In the backend**, every successful or failed probe is stored per owner, agent, auth mode,
  account (stored credential id, or `default`) and endpoint URL (table `model_catalogs`). The
  session agent pushes one probe shortly after the CLI started, so the cache fills without anyone
  opening a picker. The new-session dialog reads this cache for the chosen account; no pod is
  started for it.
- **States**: `fresh` (under 24 hours), `stale` (older, shown with its age), `unknown` (never
  probed for that account), `fallback` (the CLI answered with its bundled list after a refusal),
  `error` (the probe failed: login missing, key refused, endpoint unreachable). Refresh asks a
  running session of the same owner and account; with none, the dialog says so.
- **Manual id**: the field always accepts a typed id. A chosen model that is missing from a later
  catalog stays chosen and is marked "not in this account's list" — the session is never
  switched to another model, or another account, by a refresh.

## Selecting a model

`ModelName` already existed for custom endpoints. It now means "the model the session starts on"
for every auth mode: required for a custom endpoint as before, optional otherwise (empty = the
CLI's default). The pod gets it as `AGENTHUB_MODEL_NAME` and each runtime maps it: Claude
`ANTHROPIC_MODEL` in the CLI child's environment, Codex a top-level `model` in `config.toml`
(and `--model` on a resume, which ignores the config: `live-model-switch.md`), Cursor `--model`, OpenCode
the user config's `model`, ahead of the image-wide `AGENTHUB_OPENCODE_MODEL`.
OpenClaw refuses it at create, like a custom endpoint. Changing it in the edit dialog applies on
the next restart, like every other start setting; switching mid-conversation is the Workspace
picker's job (#478, `live-model-switch.md`).

## Who sees what

- **Owner and collaborators** read the session's catalog and may refresh it (a refresh spawns a
  process in the pod under the owner's credential).
- **Viewers of a shared session** read the session's cached catalog only, never refresh. They can
  already watch the terminal, where the CLI's `/model` menu shows the same list. The catalog never
  contains the account's e-mail, organisation or plan name (Claude's `initialize` reports all
  three; they are dropped in the pod), nor a credential: error texts are passed through the
  runtime's secret mask and cut to 300 characters.
- **The dialog cache** is per owner. Another user never reads it, also not through a shared
  project.

## Limits

- Claude and Codex with an API key, and OpenCode, list what the CLI knows; a model the key may not
  use fails at the first turn, as before. The catalog says "not verified".
- OpenClaw has no probe; its id stays manual.
- Cursor is unpinned, so its output format can change under the probe; an unparseable answer is
  an `error`, not an empty list, and the field stays free text. Its success path is unverified
  with a real account.
- Claude answers from the account options it cached in `~/.claude.json` at its last start and
  refreshes them in the background; the probe after that sees the update.
- The probe shares HOME with the interactive CLI. Two processes on one login is what the CLIs
  support anyway (a person running a second `claude` in the shell does the same), but Codex's
  probe starts its app-server, which also reads the plugin and account routes once.
- Switching the running session is #478's Workspace picker (`live-model-switch.md`).

## Verification

- Session agent (`session-agent/test/model-catalog.test.js`): every probe's parsing, the field
  whitelist (no e-mail, organisation, provider URL, header or option), masking of credential values
  and token shapes, the per-credential cache (switch, refresh, error, single flight), the route.
- Backend (`ModelDiscoveryTests`): validation and update/copy rules of the chosen model, the pod
  env, catalog keys (owner always part of the key), sanitising the pod's answer, a transient error
  keeping the last good list, viewer read-only access, the pod-only proxy. With Postgres: the real
  store keeps owners apart, and a viewer reads through the real share store and access rules. That
  test caught that the access check returns a partial session row; the controller now reads the
  full row.
- Real CLIs (`tests/ModelDiscoveryProof`): Codex 0.167.0 in the real `common/server.js` against a
  recording mock — the post-start push, account A, an account switch through
  `PUT /agenthub/credentials` answering for B without a refresh flag, a refused account as
  `fallback`/`not_entitled`, a custom endpoint listed from the pod and its refused key, and no
  credential in any response, hub request or log. Claude 2.1.318 (API key against the mock,
  hooks and plugin with a negative control), Cursor's login errors, OpenCode 1.18.35 with a key in
  a custom provider's options.
- Chromium against the real catalog, session and internal controllers on Postgres, the real
  session agent and the real Codex CLI: the new-session dialog for account 1, a switch to account 2
  and Refresh, a refused account, an unlisted typed id kept, a custom endpoint looked up without a
  single request from the hub to it, 390 px, and a viewer reading without refresh or account ids.
