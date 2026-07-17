# Claude Design prompt — Open AgentHub web UI

Paste the prompt below into Claude Design (claude.ai/design).

---

## Prompt

You are designing **Open AgentHub**, a self-hosted web app to run and supervise AI
coding agents ("sessions") that live in a Kubernetes cluster and work 24/7 on the
user's repositories. Today the UI is a functional but developer-centric dark
terminal tool. Redesign it into a **calm, modern, role-adaptive product** with a
reusable **design system**, without removing any existing capability.

### Who uses it (design for all of them, not just developers)
- **Developer** — power user; lives in the live terminal and the in-pod shell,
  starts many sessions, cares about repos/branches, MCP tools, allowed-tools, logs,
  keyboard speed.
- **Team lead / engineering manager** — oversight; wants an at-a-glance view of what
  agents are doing, what is waiting on a human, token/cost spend, seat usage, and
  the billing/license state. Rarely opens a terminal.
- **Product owner** — outcome-focused; starts an autonomous or scheduled task from a
  plain-language brief, follows progress, answers the agent's questions (also from
  Slack), reads a readable summary rather than raw terminal output.
- **Normal / occasional user & hobbyist** — wants a dead-simple "give the agent a
  task and check back later" flow on desktop and phone, with almost no jargon.

The same screens must serve all of them via **progressive disclosure**: a simple,
guided primary path with advanced controls (custom image, run-as-root, CPU/RAM,
MCP config, allowed-tools) behind an "Advanced" affordance. Prefer plain language
with a short technical subtitle over raw jargon.

### What exists today (keep every capability; improve the experience)
- **Sessions list** — status (running / waiting-for-reply / succeeded / failed /
  scheduled / paused), text search, per-session resume / pause / edit / delete.
- **Session detail** — tabs **Agent** (live xterm terminal) and **Shell** (an
  interactive bash in the same pod); a mobile reply box; **pause/resume**; a saved
  transcript for finished sessions; the session id lives in the URL (deep links).
- **New session** — title; mode (interactive / autonomous / scheduled); one or more
  **repositories** via a searchable picker across connected Git accounts (+ manual
  URL); prompt; cron schedule; allowed-tools; MCP config; container image;
  run-as-root; CPU/RAM.
- **Account** — connect GitHub/GitLab accounts via OAuth (incl. self-hosted).
- **Credentials** — SSH key, tokens, git identity (write-only, "stored ✓" chips).
- **Settings** — personal API tokens for the remote API (create once, copy, revoke);
  per-user **Slack** notification preferences (auto-connected via email, opt-out,
  channel override).
- **Usage** — token & cost dashboard (per session and total), fed by the agents'
  OpenTelemetry metrics.
- **Enterprise features require a valid license** (Slack notifications today; more
  later). Everything enterprise is gated on the license being active.

All settings - should be run inside the a settings page. provide for admin two parts - normal user settings for their own settings and admin settings with user management, license management etc.
Usage data must not be provided inside settings. it can be somewhere outside because its interesting for everyone.

Do not use popup dialogs.

### New in this redesign — licensing, admin & billing
Open AgentHub is open-core: the core is free (AGPL), enterprise features need a paid
subscription. The license is **activated inside the app and stored in the database**
(no config/helm flag). Design these flows:

1. **License activation / status** — a first-run and Settings screen where an admin
   pastes/enters the license key (or starts a trial), which is then verified and
   stored server-side. Show current plan, seat count (used / included), validity /
   expiry, and a clear "Community (unlicensed)" vs "Enterprise (active/expired)"
   state. When unlicensed, enterprise features are visibly locked with an "upgrade"
   affordance — the core product stays fully usable.
2. **User & seat administration** (admin only) — a table of app users (who has
   signed in), each consuming a seat; grant/revoke a user's seat; show used-vs-
   included seats and a warning when over the limit. By default every user who logs
   in gets a seat.
3. **Billing & cost** (admin only) — current subscription and spend overview
   (this period + historical), a **list of invoices** with download (the last N
   legally required, or all), and a **cancel subscription** flow (with confirmation).
   These talk to a billing backend; design the states (loading, empty, past-due,
   canceled).


### Deliverables
1. A **design system**: color tokens (refined **dark** default + a clean **light**
   theme), typography scale (UI sans + mono for terminals/code/IDs),
   spacing/radius/elevation, and states (hover/focus/active/disabled/loading/empty/
   error). Accent is blue (#5AA9F5) — evolve it into a full **WCAG-AA** palette with
   semantic colors for the session states and for locked/enterprise elements.
2. A **component library**: buttons, inputs, selects, search, tabs, modal/sheet,
   cards, list rows, status & "waiting for reply" badges, chips, tables, empty
   states, toasts, a metric/stat tile + a small chart style for usage/cost, a
   "locked / enterprise" treatment, and a responsive app shell (top bar + sidebar
   that collapses to mobile).
3. **Key screens**, desktop and mobile:
   - App shell & role-aware navigation (dashboard, sessions, usage; admin/billing
     only for admins).
   - **Dashboard / overview** (new): active sessions, what's waiting on a human,
     token/cost this period, seat usage, license state — the manager/PO landing.
   - Sessions list (search, filters, status incl. paused).
   - Session detail with **Agent / Shell / Transcript** tabs, pause/resume/edit/
     delete, and an unmissable-but-calm "the agent is asking X — reply" panel that
     shines on mobile.
   - New session as a **short guided flow** (task + repos first; advanced collapsed)
     a non-developer can complete.
   - Account / Credentials / Settings (incl. Slack preferences, API tokens).
   - Usage & cost.
   - **License activation**, **user/seat admin**, **billing & invoices** (enterprise).
   - Slack message mockups (question, permission-buttons, finished).
4. **Interaction & UX notes**: waiting-for-reply must be unmissable but not noisy;
   the terminal must not flicker on resize; full keyboard reachability; strong
   empty/first-run states that teach the product; a coherent voice (confident,
   plain, a little playful — "be your own digital team lead").

### Constraints
- Self-hosted, dark-first, must look right on a phone (supervising on the go is a
  core use case).
- Do **not** tie the brand to any specific company; the only names are
  "Open AgentHub" and the maintainer's project identity.
- Output a design system + annotated screens (Figma-style frames) with tokens and
  components defined once and reused. Keep the existing IA where it works; improve
  flows and the visual system rather than rebuild concepts.

Start with the design tokens and the app shell, then the dashboard and the session
detail, then licensing/admin/billing, then the Slack mockups.
Have always UX and modern design in mind.
