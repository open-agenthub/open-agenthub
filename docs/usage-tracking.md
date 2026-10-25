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
- **Subscription usage**: tokens and, where the CLI records them, turns. **No dollar amount.** The
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
