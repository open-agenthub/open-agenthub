# Usage limits per account: detect, remember, switch

A subscription login has a quota — Claude's five-hour and seven-day windows, Codex's primary and
secondary windows, Cursor's dollar budget, whatever OpenClaw's upstream provider meters — and a
session that runs into it stops in a way nobody built for. An interactive session shows the CLI's
own notice and waits; an autonomous `-p` run prints the same notice and exits as *Failed*, and the
person who started it finds out in the morning. Meanwhile the same user has a second login stored
(`docs/provider-accounts.md`) that was not touched.

This file records what the hub now does about it: how it learns that an account is at its limit,
how long it remembers that, how a running session is moved to another account, and which account
a new session avoids. The MCP side — switching and asking from a tool — is here too, because an
agent that notices its own limit is the one caller that can act fastest.

## What is stored: `exhaustedUntil` on the account

`ProviderAccount` in the `accounts.json` index gains `exhaustedUntil` (a UTC timestamp) and
`exhaustedReason` (a short text such as `five_hour 100%` or the CLI line that matched). An
account is *exhausted* while `exhaustedUntil` lies in the future; once it has passed the mark
is simply ignored, so nothing has to sweep it. `ProviderAccountInfo` reports both and a computed
`isExhausted`, so every listing — the settings pane, `GET /api/remote/credentials`, the
`credentials_list` tool — says it without a second call.

The mark lives in the same secret as the login and nowhere else. The alternative was a Postgres
table keyed by owner, agent and account id, which would have been easier to query across users.
It was rejected because the secret already *is* the account: `DeleteProviderAccountAsync`,
the account purge and the lazy single-file migration all operate on that one object, and a side
table is a second place that has to be deleted, purged and migrated in step with it. The first
time somebody removes an account and re-adds it under a new id, a side table would carry a
stale mark that nothing points at.

A pinned exhausted account is still mounted. The pin is a choice the person made, and
silently starting a session on another login would hide which account is broken — the same
rule `docs/provider-accounts.md` states for a failing token. The failover below changes the
account of a *running* session and records that it did; a start never does.

`PATCH /api/credentials/accounts/{agent}/{id} {clearExhausted: true}` clears the mark by hand,
for the day the detection was wrong (see "Where the detection is weak").

## Which account a new session gets

`ProviderAccountSecret.ResolveId(set, credentialId, now)` keeps its first rule — a pinned id
wins — and changes the second: without a pin the default account is used *when it is
available*, otherwise the first available account in index order, and only when every account
is exhausted the default after all. "Available" is `ProviderAccountSet.Available(now)`: not
exhausted at this moment.

A session with no pin therefore follows the default when the default is fine, and sidesteps
it when it is not, at every start and resume. The account that was actually mounted is written
to the session record as `resolved_credential_id`, which `credential_id` does not say for an
unpinned session. It is what the detection below marks when the session reports a limit, and
what `account_status` and the session header show as "runs on".

## How the hub learns about a limit

Both paths end at one endpoint, `POST /internal/sessions/{id}/account-exhausted
{source: "mod"|"output", kind?, percentUsed?, resetsAt?, detail?}`, authenticated with the
session's callback token like every other pod callback. The hub marks the session's account —
`credential_id` when pinned, else `resolved_credential_id` — exhausted until `resetsAt`, or for
`AgentHub:AccountExhaustedDefaultSeconds` (an hour) when the report carries no reset time, and
raises the notifier event `account-exhausted` so Slack, Telegram and n8n see it.

### Claude: the mod reports what the CLI knows

Claude Code is the one runtime that *tells* a mod its rate limits. `$.session.usage()` returns
`rateLimits: [{kind, percentUsed, resetsAt}]` — `five_hour`, `seven_day`, a gateway's
`spend_limit` — and the `session.measure` event fires with the same figures after every turn
and whenever a window moves a whole point (verified against the type declarations Claude Code
publishes for 2.1.290; `agent-runtime/claude/mods/agenthub-fleet/README.md` says which version
the image build tests against). The `agenthub-fleet` mod hooks `session.measure` and, for every
window whose `percentUsed` reaches the threshold (`AGENTHUB_LIMIT_THRESHOLD`, default 100),
posts `{kind, percentUsed, resetsAt}` to the session agent's loopback route
`POST /agenthub/mod/limit` — same token and same loopback rule as the mod inbox
(`docs/priority-messages.md`). The session agent forwards it to the hub as `source: "mod"`.

The mod reports once per window and reset time: a window that stays at 100 % raises
`session.measure` again on every turn, and the hub does not need to hear it again until the
reset moved. It also watches `turn.complete`: a turn that ended with `reason: "error"` means an
API error the CLI could not retry away, and when the last usage reading already had a window at
the threshold the mod reports that too — the turn that died is the one a person would have
noticed. The mod never decides anything; the hub holds the policy, so a change of policy is a
backend deploy and not an image rebuild.

Why not `turn.step`? Its hook is an async generator over the streamed response, and the result
it sees (`stopReason`, `usage`) carries no error text; a request refused with 429 arrives as a
turn that completes with `reason: "error"`. The rate-limit figures themselves come from the
response headers Claude Code reads into `$.session.usage()`, which `session.measure` already
pushes. Hooking the stream would add a generator to every model request for information the
two simpler events already carry.

### Everyone else: the session agent reads the output

Codex, Cursor and OpenClaw have no in-process mods and their hooks expose no quota, so the
session agent (`agent-runtime/common/limit-detector.js`) watches what the CLI prints: the PTY
stream, stripped of ANSI sequences and cut into lines; the stdout lines of a chat-mode pipe
(stream-json `result` events with `is_error`, and the `error` events); and stderr. Each driver
exports `limitPatterns`, a list of `{pattern, resetsAt?}` where `resetsAt` reads a reset time
out of the matching line when the CLI prints one. The first match per agent start is reported
as `source: "output"` with the matching line as `detail`; the detector is then quiet until the
agent is started again (a restart after a credential swap resets it), so a notice the TUI keeps
on screen does not become a report per redraw.

The patterns, and where each string was verified. The sentences are quoted as the binary
holds them; `…` marks text that varies (a plan name, a time):

| Runtime | Verified against | Strings | Reset time |
|---|---|---|---|
| Codex | `@openai/codex@0.160.0` (the pinned version; strings read from the `win32-x64` binary in that package) | `You've hit your usage limit.` / `You've hit your usage limit for …`; `Usage limit reached` (TUI headline) and `You've reached your usage limit.`; `exec --json` error codes `usage_limit_reached`, `rate_limit_reached`, `workspace_owner_usage_limit_reached`, `workspace_member_usage_limit_reached`, `quota_exceeded` | `Try again at …` in the TUI text; `resets_at` / `reset_at` / `reset_after_seconds` in the JSON error body |
| OpenClaw | `openclaw@2026.7.1-2` (the pinned version; strings in `dist/*.js`) | `Your Codex usage limit is reached.`, `… Codex usage limit is reached`, `… check your account for subscription or usage limits, then try again.`, `API rate limit reached. Please try again later.`, `The model provider returned HTTP 429 before replying.` | none printed; default |
| Claude (fallback only, when no mod heartbeat is alive) | `@anthropic-ai/claude-code@2.1.285` (the locally installed binary; the runtime pins 2.1.287, whose string table was not read) | `You've hit your limit`, `You've hit your usage limit`, `Usage limit reached`, `You're out of extra usage`; `-p` result lines with `subtype: "error_during_execution"` whose text matches those | `resets at …` / `resets in …` / `resets …` as the CLI formats them |
| Cursor | **unverified** — the CLI is unpinned (`docs/agent-runtime-updates.md`); the `2026.10.01` package served by `cursor.com/install` was unpacked and its JavaScript contains no usage-limit sentence of its own, only `budget_exceeded` for a run's dollar budget, so the limit text seen in a terminal comes from the API response the CLI prints verbatim | conservative: `usage limit`, `hit your … limit`, `budget_exceeded` | none |

None of the Claude strings is in a test as a Claude *expectation*: the Claude runtime reports
through the mod, and the fallback exists for a CLI started without one (a custom image, a
mod the user disabled). The output path is the fallback for Claude, not the mechanism.

### Where the detection is weak

A pattern over terminal output can match text the agent *reads* rather than text the CLI
*prints*: a tool result that quotes this document, a test fixture, a log line from somebody
else's CLI. The cost of a false positive is one account marked for an hour and, when another
account exists, one account switch with a restart-and-resume; the person sees both (banner,
chat message, notifier event) and can clear the mark. That is why the output patterns are the
CLI's whole sentences and not the words `rate limit` — GitHub's API prints those into every
third `gh` call — and why a Claude session with a live mod ignores the patterns entirely: the
mod's figures come from response headers, not from text.

The other weakness is the reset time. Only the mod and Codex's JSON error carry one the hub can
parse; a TUI line such as `Try again at 3:15 PM` is in the user's locale and time zone, which the
pod does not share with the person, so it is kept as `detail` and the default hour applies.
An hour is short enough that a wrong guess costs little and long enough that the session is
not switched back and forth within one five-hour window.

## Automatic switch

`AccountFailover` runs after every report. When the session is `Subscription`, its pod is
`Running`, `account_failover` is not `off`, and the owner has another account of the same
agent that is not exhausted, it calls the same `SwitchSessionCredentialAsync` the header
dropdown calls: the record is re-pointed, the file is pushed to the pod, the agent restarts
with resume. Candidates are ordered default first, then by `lastUsedAt` ascending with
never-used accounts ahead of used ones — the login that has sat idle longest is the one with
the most quota left, which is a guess, but a better one than index order.

After the switch the session is told. A priority message from outside the fleet
(`docs/priority-messages.md`) carries `Switched to account <label> because <old label> hit its
usage limit (resets <time>)`, so a Claude session reads it through its mod as the next prompt
and a Codex or Cursor terminal gets it typed in once the TUI is back; the credential push
carries the reason as a header so the restart line in the scrollback — and the `agenthub`
event the chat UI shows — names the switch rather than a generic "account switched"; and the
notifier raises `account-switched`. Without a candidate the session stays where it is, the
notifier raises `account-exhausted` with "no other account available", and the session view
shows a banner off the same accounts listing it already loads.

The policy is per session: `accountFailover` is `auto` (default) or `off` on create, update,
duplicate, the remote PATCH and every `session_create`, stored in `sessions.account_failover`
(null reads as `auto`). Off is for the person who runs one session per login on purpose — a
test that must not bill the team plan, a demo on a specific organisation — and would rather
have the session stop than drift.

An autonomous `-p` run is switched like any other: the pod is Running until the CLI exits, and
the restart uses the provider's resume command. For Claude that continues the same
conversation (`--resume <fixed session id>`), for Codex the recorded thread
(`docs/session-mode-conversion.md` has the table). Cursor and OpenClaw resume into a fresh
conversation in the same workspace, so their autonomous run starts its prompt from the top on
the new account — which is still the run finishing, rather than the run failing at 03:00
with the prompt half done. The alternative, pausing the session and resuming it later from the
archive, would lose the pod's local state for exactly the two runtimes that cannot resume
anyway, and would leave Claude and Codex waiting for a person for no reason.

## MCP and the remote API

`account_status {sessionId?}` answers with the session's agent, the account it runs on (label,
`isExhausted`, `exhaustedUntil`), its `accountFailover` setting and the alternatives — the
owner's other accounts of that agent with their own `isExhausted`. `account_switch {sessionId,
credentialId}` moves a running session, with the same rules and errors as the header dropdown.
Both exist on the remote MCP, the stdio MCP (owner scope) and the in-pod MCP, where
`sessionId` may be omitted for the session itself and otherwise has to name a descendant, as
`session_get` requires. A session switching *itself* is allowed on purpose: an agent that
reads "usage limit" in its own tool output can call `account_status`, see an alternative and
`account_switch` to it, and that is a better outcome than waiting for the hub's pattern to fire
or for a person.

The remote REST surface gets `PATCH /api/remote/sessions/{id}/credential {credentialId}`,
which the stdio server calls. A token restricted to some accounts (`docs/credential-scopes.md`)
may only switch to an account it is allowed to use; anything else is 403
`credential_not_allowed`. The in-pod route goes through `/internal/sessions/{id}/credential`
and `/internal/sessions/{id}/peer/{childId}/credential`, authenticated with the callback token
as the rest of the fleet routes are.

## What was considered and not built

- **Showing the limit in the UI and stopping there.** The session view could render
  `$.session.usage()` as a gauge, and `docs/claude-code-mods.md` had that under "looking
  ahead". It does not help the case this exists for: an autonomous session at 03:00 has nobody
  looking at a gauge. The gauge is still a good idea; it is a display of the same report.
- **Polling the providers for quota.** Anthropic has no quota API for subscription logins,
  OpenAI's is tied to API keys and says nothing about a ChatGPT plan, Cursor's is a dollar
  figure behind a dashboard. Everything the hub could learn that way it learns more cheaply
  from the CLI that just hit the wall.
- **Switching at a threshold below 100 %.** `AGENTHUB_LIMIT_THRESHOLD` exists for an operator
  who wants the mod to report at 95 %, but the default is the limit itself. Switching early
  spends a second account's window on work the first could still have done, and the seven-day
  window of a plan that is 95 % used on Friday is better spent than saved.
- **Switching back when the first account resets.** The mark expires on its own, so the next
  *new* session uses the first account again; a running session keeps the account it was
  moved to. Moving it back would be a second restart for no gain.
- **Reading Codex's `/status` or `TokenCountEvent.rate_limits` from the TUI.** The TUI's
  bottom line shows the windows, and the JSON `exec` stream carries them in `token_count`
  events. Parsing a redrawn TUI line is brittle, and the `exec` stream only reaches the session
  agent in autonomous mode; the error that ends the run is simpler and is what the detector
  matches. A Codex hook exposing rate limits would make this unnecessary; none does today.
