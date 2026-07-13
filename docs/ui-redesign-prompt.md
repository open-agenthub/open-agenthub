# Claude Design prompt — Open AgentHub web UI

Paste the prompt below into Claude Design (claude.ai/design). It is written to
produce a coherent, role-adaptive redesign of the Open AgentHub control UI with a
proper design system. Attach screenshots of the current app (session list, session
terminal, New/Edit dialogs, Account/Credentials/Settings) so it can build on the
existing structure rather than inventing a new IA.

---

## Prompt

You are designing **Open AgentHub**, a self-hosted web app to run and supervise AI
coding agents ("sessions") that live in a Kubernetes cluster and work 24/7 on the
user's repositories. Today the UI is a functional but developer-centric dark
terminal tool. Redesign it into a **calm, modern, role-adaptive product** with a
reusable **design system**, without removing any existing capability.

### Who uses it (design for all of them, not just developers)
- **Developer** — power user; lives in the live terminal, starts many sessions,
  cares about repos, branches, MCP tools, allowed-tools, logs, keyboard speed.
- **Team lead / engineering manager** — oversight; wants an at-a-glance view of
  what agents are doing, what is waiting on a human, cost/token spend, and who is
  using how much. Rarely opens a terminal.
- **Product owner** — outcome-focused; starts an autonomous or scheduled task from
  a plain-language brief, follows progress, approves/answers questions, reads a
  readable transcript summary rather than raw terminal output.
- **Normal / occasional user & hobbyist** — wants a dead-simple "give the agent a
  task and check back later" flow on desktop and phone, with almost no jargon.

The same screens must serve all of them. Use **progressive disclosure**: a simple,
guided primary path with advanced controls (custom image, run-as-root, CPU/RAM,
MCP config, allowed-tools) tucked behind an "Advanced" affordance. Prefer plain
language with a short technical subtitle over raw jargon.

### What exists today (keep the capabilities, improve the experience)
- **Sessions list** with status (running / waiting-for-reply / succeeded / failed /
  scheduled), search, per-session resume / edit / delete.
- **Session detail**: a live terminal (xterm) with a mobile reply box; a resume
  button; a saved transcript for finished sessions. (Soon: a separate shell tab and
  a pause action.)
- **New session**: title, mode (interactive / autonomous / scheduled), one or more
  repositories (searchable picker across connected Git accounts + manual URL),
  prompt, cron schedule, allowed-tools, MCP config, container image, run-as-root,
  CPU/RAM.
- **Account**: connect GitHub/GitLab accounts (OAuth).
- **Credentials**: SSH key, tokens, git identity (write-only, "stored ✓" chips).
- **Settings**: personal API tokens for the remote API (create once, copy, revoke).
- **Enterprise (licensed)**: Slack integration; and planned — an **admin area** for
  user/seat management, a **token-usage / cost dashboard**, and **billing** (view
  spend, fetch invoices, cancel subscription via Stripe).

### Deliverables
1. A **design system**: color tokens (dark **and** light theme; the app is dark
   today — keep a refined dark as default and add a clean light theme), typography
   scale (UI sans + monospace for terminals/code/IDs), spacing/radius/elevation
   scale, and states (hover/focus/active/disabled/loading/empty/error). Accent color
   is currently blue (#5AA9F5) — evolve it into a full accessible palette
   (WCAG AA), including semantic colors for the session states.
2. A **component library**: buttons, inputs, selects, search field, tabs, modal /
   sheet, cards, list rows, status badges & the "waiting for reply" indicator,
   chips, tables, empty states, toasts, a metric/stat tile and a small chart style
   for the cost/usage dashboard, and a responsive app shell (top bar + sidebar that
   collapses to a mobile layout).
3. **Key screens**, desktop and mobile:
   - App shell & navigation (role-aware: an overview/dashboard home, sessions,
     admin/billing entry points only when permitted).
   - **Dashboard / overview** (new): active sessions, what's waiting on a human,
     token spend & cost this period, seat usage — the manager/PO landing view.
   - Sessions list (with search, filters, status).
   - Session detail with **tabs**: Agent (live terminal), Shell, Transcript — plus
     resume / pause / edit / delete and a clear "the agent is asking you X — reply"
     panel that works great on mobile.
   - New session as a **short guided flow** (task + repos first; advanced settings
     collapsed) that a non-developer can complete.
   - Account / Credentials / Settings.
   - **Admin & billing** (enterprise): licensed-users management, token-usage & cost
     dashboard, invoice list/download, cancel-subscription flow.
4. **Interaction & UX notes**: the waiting-for-reply state must be unmissable but not
   noisy; the terminal must not flicker on resize; everything reachable by keyboard;
   strong empty/first-run states that teach the product; a coherent voice
   (confident, plain, a little playful — "be your own digital team lead").

### Constraints
- Self-hosted, single dark-first product; must look right on a phone (supervising on
  the go is a core use case).
- Do **not** tie the brand to any specific company; the only names are
  "Open AgentHub" and the maintainer's project identity.
- Output as a design system + annotated screens (Figma-style frames) with the tokens
  and components defined once and reused. Keep the existing information architecture
  where it works; improve flows and visual system rather than rebuild concepts.

Start by proposing the design tokens and the app shell, then the dashboard and the
session-detail screen, then the rest.
