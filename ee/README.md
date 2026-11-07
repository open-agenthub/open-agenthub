# Open AgentHub Enterprise Edition (`ee/`)

This directory contains the source code of the
**Open AgentHub Enterprise Edition** — features aimed at teams and
organizations. Unlike the rest of this repository, the code in `ee/` is
**not open source**: it is source-available under the
[Open AgentHub Enterprise License](./LICENSE) and requires a valid
subscription for production use.

## Enterprise features

All features below are **available in 1.18.0** and require a valid Enterprise
license. Links lead to the user guides.

- **Slack integration — available** — session questions appear in threads; replies,
  `!status` and permission buttons accept input only from the session owner or a
  Collaborator, matched by an email belonging to exactly one AgentHub user.
  Viewers cannot reply. `!new [+repo …] <prompt>` starts an autonomous session and
  `!repos [query]` lists projects. Uses Socket Mode, with no public endpoint.
  See [Slack setup and limits](https://open-agenthub.github.io/docs/slack.html).
  Sources: [`backend/Slack/`](./backend/Slack).
  Telegram and Signal remain Community features.
- **Session sharing and lifecycle controls — available** — owners grant named
  users or secret-link holders Viewer or Collaborator access. Viewers watch;
  Collaborators can also type and resize the terminal. Owners can change roles,
  revoke grants and links, and set link expiry. Settings, shell access, lifecycle
  and sharing management remain owner-only.
  See [sharing and roles](https://open-agenthub.github.io/docs/sharing.html).
- **Project sharing — available** — a project owner shares with a user or one of
  their own identity-provider groups. Access covers that owner's current and future
  sessions in the project and their children, including children outside the project.
  Project grants are managed in the web app, not through personal tokens or MCP.
  See [project sharing](https://open-agenthub.github.io/docs/sharing.html#share-a-whole-project).
- **Inherited child access — available** — named shares reach same-owner child
  sessions with the inherited role. The strongest applicable grant wins; removing
  one grant removes only its access on the next authorization check. Existing
  connections and downloaded copies are not recalled. Share links open only their
  target session, not its children.
  See [child sessions](https://open-agenthub.github.io/docs/sharing.html#child-sessions-inherit-the-share).
- **Shared session discovery and messaging — available** — named, inherited and
  project shares appear under **Shared with me** and through the in-session sessions
  MCP, external HTTP MCP and personal-token stdio MCP. Recipients can list and read
  status; Collaborators can send audited normal-priority messages. Priority,
  interrupt and session management remain limited to the owner.
  See [in-session tools](https://open-agenthub.github.io/docs/session-tools.html#sessions-shared-by-other-users-enterprise)
  and [external MCP clients](https://open-agenthub.github.io/docs/mcp.html#sessions-shared-with-you).
- **Share-link MCP tools — available** — `share_link_read` returns status and a
  history page; `share_link_send` queues an audited normal message through a
  Collaborator link. Viewer links cannot send. The stdio tools need no personal
  token for these two calls; `/mcp/shared` exposes only these tools without sign-in
  when remote MCP is enabled. A valid link is still required and acts as a password.
  See [using a share link](https://open-agenthub.github.io/docs/mcp.html#use-a-share-link).
- **Available conversation history for shared readers — available** — named and
  link recipients read the available live or stored history through the web, remote
  API and MCP, subject to their role and paging. Pages contain up to 500 entries
  and approximately one million characters; unavailable or never-stored content is
  not recovered. Detected session access tokens and inline MCP credential values
  are masked; masking does not detect every possible secret.
  See [shared history](https://open-agenthub.github.io/docs/sharing.html#history-of-a-shared-session).
- **Sharing from an API client — available** — owners manage session grants and
  links through the personal-token API and the external MCP tools `session_share`,
  `session_unshare`, `session_share_link` and `session_shares`. These do not manage
  project shares. Without a license, sharing calls return `402` / `license_required`.
  See [API and MCP sharing](https://open-agenthub.github.io/docs/sharing.html#managing-shares-from-an-api-client).
- **Session-wide MCP restrictions — available** — owners block servers or exact
  tools for everyone, including themselves and child sessions. Enforcement before
  execution applies to Claude, Codex and OpenCode; Cursor and OpenClaw do not have
  the per-call check. See [MCP restrictions](https://open-agenthub.github.io/docs/sharing.html#mcp-restrictions-for-the-whole-session).
  Sharing sources: [`backend/Sharing/`](./backend/Sharing).
- **Org-wide usage limits — available** — admins set monthly API budgets globally,
  per user group or per user; the strictest applies alongside personal Community
  limits. See [usage limits](https://open-agenthub.github.io/docs/usage.html#organisation-wide-limits).
  Sources: [`backend/Usage/`](./backend/Usage).
- **Groups and roles from OIDC — available** — groups read at sign-in can map to
  admin roles; the groups claim is configurable via `Ee:Groups:Claim`.
  See [groups and roles](https://open-agenthub.github.io/docs/admin.html#groups-from-the-oidc-token).
  Sources: [`backend/Identity/`](./backend/Identity), [`backend/Usage/`](./backend/Usage).
- **Allowed agent kinds — available** — admins optionally restrict agent kinds.
  An empty list, or no valid license, leaves all agents available.
  See [allowed agents](https://open-agenthub.github.io/docs/admin.html#allowed-agents).
  Sources: [`backend/Agents/`](./backend/Agents).
- **Library sharing — available** — owners share saved MCP entries with users,
  groups or everyone; admins share skills and may allow users to publish their own
  skills to everyone. Personal libraries remain Community features.
  See [MCP entry sharing](https://open-agenthub.github.io/docs/mcp-servers.html#sharing-your-entries-enterprise)
  and [skill sharing](https://open-agenthub.github.io/docs/skills.html#sharing-skills-enterprise).
  Sources: [`backend/Library/`](./backend/Library).

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
