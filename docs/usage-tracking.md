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
| `Auto`, interactive, key and login stored | `unknown` | no |
| `Auto`, interactive, only a key stored | `api` | yes |

The `Auto` rows come from the pinned CLI, not from guesswork:

- `claude -p` (unattended) with both a login and `ANTHROPIC_API_KEY` sent `x-api-key`: the key wins.
- The interactive TUI with both asks *"Detected a custom API key in your environment. Do you want
  to use this API key?"* with **No** preselected (`proofs/258/claude-auto-tui.mjs`). The person's
  answer decides, and the hub never sees it. Such a session is `unknown`: its tokens show, it does
  not count against the API budget, and its row says the CLI chose. With only a key stored, the
  key is the one way the session can work, so it is `api`.

**The decision is hub-side state only.** Each start computes the classification from what the
hub mounts (`UsageBilling.ForPod`) and stores it on the session row (`sessions.usage_billing`).
Ingest reads the auth mode and that column, nothing else. The first version of this change sent
the label to the pod as an OTLP resource attribute and read it back from the metrics. That was
rejected in review: the OTLP endpoint is unauthenticated, and anything in its payload — a
`billing` attribute, a `user.id` — is whatever the sender wrote. A test posts a payload that
claims a subscription and a different owner for an API-key session; neither changes the result.
The pod cannot change the column either. Its service account has no bindings and no mounted
token, which `tests/helm/agent-service-account.ps1` checks against the rendered chart with every
RBAC-adding feature switched on.

**The start gate uses the same classification.** `UsageLimitService.EnsureCanStartAsync` takes
the billing `ForPod` computes for the start, on create, resume, runtime update and restart alike.
So an unattended Auto run with a stored key is refused over budget, even with a login stored.
Before, a stored login let it through, while the CLI then billed the key.

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
