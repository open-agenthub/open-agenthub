# Open AgentHub Enterprise Edition (`ee/`)

This directory contains (and will contain) the source code of the
**Open AgentHub Enterprise Edition** — features aimed at teams and
organizations. Unlike the rest of this repository, the code in `ee/` is
**not open source**: it is source-available under the
[Open AgentHub Enterprise License](./LICENSE) and requires a valid
subscription for production use.

## Enterprise features

- **Slack integration** ✅ — when a session waits for input you are asked in a
  Slack thread (with the new terminal output), and your thread replies are sent
  back to the agent. Uses Socket Mode (no public endpoint). Backend sources under
  [`backend/Slack/`](./backend/Slack); configured via `ee.slack.*` and gated by a
  valid license (`license.token` / `license.publicKey`). See the chart `values.yaml`.
  (Telegram and Signal chat integrations are available license-free in the Community
  Edition — see the [top-level README](../README.md#chat-integrations).)



- **Session sharing** — owners can share a live session with a known user or a secret link.
  A Viewer can view terminal output and transcripts. A Collaborator can also send terminal
  input and resize events.
- **Share lifecycle controls** — owners choose the role, may set link expiration, and can
  change roles or revoke direct grants and links at any time.
- **Session-wide MCP restrictions** — owners can block MCP servers or exact tools for the
  shared session. The policy applies to every participant, including the owner, and is
  enforced before tool execution.
- **Owner-only controls** — sharing does not grant shell access, settings, project,
  duplication, lifecycle, or sharing-management permissions.
- **Org-wide usage limits** ✅ — admins set monthly API budgets globally, per user
  group, or per user (the strictest limit wins; personal limits from the Community
  Edition still apply on top). Sources under [`backend/Usage/`](./backend/Usage);
  managed in *Settings → Usage limits & groups*.
- **Groups & roles from OAuth** ✅ — user groups are read from the OIDC token's
  groups claim (configurable via `Ee:Groups:Claim`) and can be mapped to roles:
  members of an **admin** group get admin access without listing each username in
  `Ee:Admins`. Sources under [`backend/Identity/`](./backend/Identity).
- **Allowed agent kinds** ✅ — admins can optionally restrict which agent kinds
  (Claude, Codex, Cursor, OpenClaw, …) may be used. An empty whitelist means no
  restriction (all agents allowed); a non-empty list is an exact allowlist.
  Without a valid license every agent stays available. Sources under
  [`backend/Agents/`](./backend/Agents); managed via `GET`/`PUT`
  `/api/admin/allowed-agents`.
- **Library sharing (MCP servers & skills)** — administrators can share saved MCP
  servers and skills with individual users, user groups, or everyone on the instance;
  user groups are managed in the admin area. Optionally, admins can allow regular
  users to publish their own skills to everyone. Backend sources under
  [`backend/Library/`](./backend/Library). (Saving personal MCP servers and skills
  for your own reuse is a Community feature and needs no license — only sharing
  between users is enterprise.)

## Subscription

Using the Enterprise Edition in production requires a valid
**Open AgentHub Enterprise subscription**:

- **6 € per user per month** (excl. VAT)
- **3-month free trial**

Pricing and sign-up: https://open-agenthub.github.io

## What about the rest of the repository?

Everything **outside** `ee/` is licensed under the
[GNU AGPL-3.0](../LICENSE) and is **fully functional without any
license key or subscription**. The Community Edition is not a demo:
you can self-host and use it in production, free of charge, forever.
