# Usage tracking: tokens for everyone, money only where someone pays

The Usage page and `GET /api/usage/*` show what each session consumed. This file records where
the numbers come from and how the hub decides whether a number is money. The data is
owner-scoped: every query filters on the caller's owner, and the owner of a row comes from the
sessions registry, never from the payload that delivered the numbers.

## Who pays is decided by what the pod got, not by the reported cost

Claude Code exports `claude_code.cost.usage` over OpenTelemetry. The hub used to treat that figure
as the bill whenever it was above zero (`ApiBilled = AuthMode == "ApiKey" || CostUsd > 0`), on the
assumption — written into `ClaudePricing` — that a subscription session reports 0.

That assumption is wrong. Against a recording mock model (`proofs/258/mock-model.mjs`, OTLP
decoded with `proofs/258/otlp-dump.mjs`), Claude Code 2.1.287 exported **$0.003255** for one
identical turn both with `ANTHROPIC_API_KEY` (request carried `x-api-key`) and with a subscription
login (request carried an OAuth bearer token). The CLI prices tokens from its own table whatever
the credential. So every subscription session counted as API spend: in the per-session table, in
the "API cost" total, and in `usage_monthly`, which feeds the monthly API budget. Enough
subscription use could block a person's real API-key sessions.

Now `UsageBilling` classifies each report:

| Session auth mode | Billing | Counts as spend |
|---|---|---|
| `ApiKey` | `api` | yes — the cost the CLI reported |
| `Subscription` | `subscription` | no |
| `CustomEndpoint` | `custom` | no (self-hosted, no provider bill) |
| `Auto`, unattended (`autonomous`/`scheduled`) | `api` if an API key is stored, else `subscription` | if `api` |
| `Auto`, interactive | `unknown` | no |

The `Auto` rows come from the pinned CLI, not from guesswork:

- `claude -p` (unattended) with both a login and `ANTHROPIC_API_KEY` sent `x-api-key`: the key wins.
- The interactive TUI with both asks *"Detected a custom API key in your environment. Do you want
  to use this API key?"* with **No** preselected (`proofs/258/claude-auto-tui.mjs`). The person's
  answer decides, and the hub never sees it. Such a session is `unknown`: its tokens show, it does
  not count against the API budget, and its row says the CLI chose.

The pod-spec factory computes the label from what it mounted (`UsageBilling.ForPod`) and passes it
as the OTLP resource attribute `agenthub.billing`. The hub uses that label **only for Auto**. For
the other modes the session record decides, because the OTLP endpoint is unauthenticated and a
label in the payload must not be able to move API-key spend out of the budget.

**Alternative considered:** read the credential the CLI actually used from its own state (Claude
keeps approved and rejected key suffixes in `~/.claude.json`). Rejected: that is key material, and
the session agent must send the hub only numeric usage, never credentials.

**Alternative considered:** keep `cost > 0` and subtract an estimate for subscription sessions.
Rejected: it keeps a price table in the decision, and a price table cannot tell who was billed.

## Where the numbers come from, per CLI

Each CLI already records its own usage. The hub reads that record and does not count tokens
itself. Claude Code exports OTLP metrics, which the hub has ingested since before this change.
The others write files, which the session agent reads on its 30-second persistence tick
(`driver.readUsage`, `agent-runtime/common/usage.js`). It posts the session's **cumulative
totals** to `POST /internal/sessions/{id}/usage`, and again on exit. The exit report matters:
an autonomous run is often shorter than one tick.

Verified against the pinned versions with a recording mock model that answers every request
with input 1000 (600 of it cached) and output 50 (20 of it reasoning):

| CLI (pin) | Source | Tokens | Turns | Cost | Recorded |
|---|---|---|---|---|---|
| Claude Code 2.1.287 | OTLP `claude_code.token.usage` / `.cost.usage` | input (uncached), output, cacheRead, cacheCreation, per model | not exported | its price table, **for a subscription too** | yes, needs `telemetry.enabled` |
| Codex 0.160.0 | `$CODEX_HOME/sessions/**/rollout-*.jsonl`: one `token_usage_record` per response (`response_id`), `event_msg` `task_started` per turn | `input_tokens` **includes** cached, `cached_input_tokens`, `cache_write_input_tokens`, `output_tokens` incl. `reasoning_output_tokens` | yes | none | yes |
| OpenCode 1.18.35 | `~/.opencode/opencode.db` (SQLite), `message.data` of assistant messages | `tokens.input` (uncached), `tokens.output` **excl.** `tokens.reasoning`, `tokens.cache.read/write` | user messages of top-level sessions | `cost` from its model price table | yes |
| OpenClaw 2026.7.1-2 | `~/.openclaw/agents/*/sessions/<id>.jsonl`, `message.usage` of assistant entries | `input` (uncached), `output` incl. `reasoningTokens`, `cacheRead`, `cacheWrite` | user messages | `usage.cost.total` from its price table | yes |
| Cursor (unpinned) | — | — | — | — | no |

All three file readers produced the same totals from that run: 400 uncached input, 600 cache
reads, 50 output (20 of it reasoning), 1 turn. The CLIs disagree on what "input" and "output"
contain, and each reader maps its CLI onto the hub's buckets. Input excludes the cache, and output
includes reasoning, as providers bill it.

- **Codex** de-duplicates by `thread_id` + `response_id`, and reads every rollout under the
  session's `CODEX_HOME` plus `archived_sessions/`. The home is per session and travels in the
  state archive, so a `/new` thread or a `codex` started in the shell tab counts too. Codex
  computes no price. An API-key Codex session therefore shows "not reported" and does not count
  against the budget. A Codex price table in the hub was rejected: it would turn tokens into a
  number nobody billed, which is the mistake this file is about.
- **OpenCode** is one aggregate query per tick over its database, through `node:sqlite` read-only.
  The database is the only place OpenCode keeps usage. Its policy plugin sees `message.updated`
  too, but a plugin restarts with every process and would have to rebuild the history from the
  same database anyway.
- **OpenClaw** skips the `.trajectory.jsonl` beside each session file. It repeats the same
  responses and would double every count.
- **Cursor** is not recorded. The CLI cannot be run without a real API key ("The provided API key
  is invalid"), so no file or event could be verified, and nothing is read on a guess.

The readers are incremental. They keep each JSONL file's offset and parse only appended lines.
A rollout of a long session is many megabytes, and re-reading it every 30 seconds would cost CPU
in every pod. A file that got shorter was rewritten and is read again from the start.

**Totals, not deltas.** The hub stores the previous report (`rep_*` columns) and adds only the
difference, so a lost or repeated report neither drops nor double-counts usage. If any total
goes down, the CLI's record started over (a fresh pod without the archived state), and the whole
report counts as new, the same rule as the pod resource counters. Owner and billing come from the
session record. A report for a Claude session is ignored, because its tokens already arrive over
OTLP and would be counted twice.

**What leaves the pod.** Only the seven numbers: `inputTokens`, `outputTokens`,
`cacheReadTokens`, `cacheCreationTokens`, `reasoningTokens`, `turns` and `costUsd`. The
files these come from hold the whole conversation. `common/server.js` therefore rebuilds the
body from that fixed list whatever a driver returns, and `usage.test.js` /
`common-server-usage.test.js` feed prompts, answers and session titles through every reader and
assert that none of it reaches the request.

## What is stored

`session_usage.cost_usd` stays what the CLI reported, kept for reference. `api_cost_usd` holds the
reported cost of the API-billed part only, and is what the "API cost" total, the per-row money and
`usage_monthly` (the budget) use. The classification is applied per report, so a session whose
billing changes mid-life, such as an Auto run converted to interactive, splits correctly.

Rows written before this change are classified once at startup (`billing IS NULL`): an `ApiKey`
row keeps its cost as spend, a `Subscription` row does not, and an old `Auto` row becomes
`unknown`. The current month's `usage_monthly` total is not rewritten. It has no per-session
breakdown to correct from, so subscription cost already counted this month stays counted until the
month rolls over.

## What the UI shows

- **API cost**: money, labelled as reported by the CLI (`Claude Code`, …), for `api` rows only.
- **Subscription usage**: tokens and, where the CLI records them (not Claude's OTLP), turns. **No dollar amount.** The
  previous "Covered by subscription ~$X" card showed a price-table estimate as if it were a value
  received. Nobody pays it, and next to real spend it read as a bill. `estimatedCostUsd` and
  `subscriptionEstimatedCostUsd` stay in the API, documented as estimates, but the UI no longer
  shows them.
- Rows that are `custom` or `unknown` show a pill and no money. An `unknown` row's tooltip names
  the cost the CLI reported, so it can still be found.

## Not changed here

The start-time budget gate (`UsageLimitService.EnsureCanStartAsync`) still lets an `Auto` session
start when a login is stored. An unattended Auto run with a key stored bills the API (see above),
so over budget such a run is not blocked. Its usage is now recorded as `api` and does count.
