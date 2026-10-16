# Sharing a session from an API client

The "↗ Share" dialog lets a session owner grant a known user a role, mint a secret link, and
revoke either. Until now that was the only way in: `SharingController` sits behind the interactive
login, so a script holding a personal `oah_` token, or an agent talking to the MCP server, could
create a session but could not hand it to a colleague. This file records what was added so that
it can, and the alternatives each decision rejected.

## One service, three callers

Sharing is now reachable from three surfaces — the web app (`api/ee/sessions/{id}/shares`), the
token API (`api/remote/sessions/{id}/shares`) and the MCP tools (`session_share`,
`session_unshare`, `session_share_link`, `session_shares`, on both the stdio and the remote
server). All three call `ISessionSharingService` and nothing else.

**The alternative was to let each controller use `SessionShareStore` directly**, which is what
`SharingController` did before. It was rejected because the one rule every surface has to agree
on — *no licence, no sharing* — would then live in three places. The web controller had a private
`LicenseFailure()` helper; the remote controller would have needed its own, and the MCP tools a
third. The first time one of them was added without the check, a Community instance would have
been able to share through that door while the other two said 402. The service checks the licence
once and throws `LicenseRequiredException`; each surface only decides how to spell that (402 on
HTTP, `license_required` on MCP).

The service also owns the link URL. A link's token exists only at creation, so the response has
to carry something a person can open, and the three surfaces had three ideas of what that is: the
web dialog showed a path, the token API is required to return an absolute URL (its caller is not
in a browser and has no origin to resolve against), and an MCP tool is in the same position. The
service builds `FrontendOrigin + /shared/{token}` once, with the same reasoning as `SessionInfo.Url`
(`api-created-sessions.md`, "The link handed to a person"): never from the request host, because a
forwarded `Host` header would send the recipient wherever the forwarder claimed to be. Unlike
`SessionInfo.Url` it does not fall back to `null` — a null here would discard the only copy of the
token — but to the path alone, which is what the dialog showed before and still resolves inside the
web app.

## The error a recipient check needs to be

The store already refused to share with a username that has never signed in
(`ArgumentException`, "Recipient is not a known user"), and `SharingController` turned every
`ArgumentException` into a 400 with the message. That is fine for a dialog that shows the text to a
person. An MCP client, or a script, has to *branch* on the reason — retry after the colleague logs
in, as opposed to fixing a role name — and the task asked for the code `unknown_recipient`.

Matching on the message text was the obvious shortcut and was rejected: it would have tied the MCP
error code to the wording of an English sentence in the store, and the next person to reword it
would silently turn `unknown_recipient` into a generic failure. The store now throws
`UnknownRecipientException`, a subclass of `ArgumentException`. Existing `catch (ArgumentException)`
handlers keep working unchanged, and the surfaces that need to tell it apart catch the subclass.
The remote API answers `400 {error, code: "unknown_recipient"}`, and the stdio server passes that
`code` through instead of its generic `agenthub_http_400` — the one case where the client reads an
error body, because the status alone does not say what to do next.

## Why the token surface has its own controller

The new routes live in `RemoteSharingController` under `ee/backend/Sharing/`, not in
`RemoteController`. Two reasons:

- They are Enterprise code. `RemoteController` is AGPL core, and the sharing tables, models and
  service are all under `ee/`; a core controller calling into them would blur a line this
  repository keeps on purpose.
- `RemoteController` is the single most contended file in the current work (four parallel packages
  touch it). A controller of its own keeps the merge to one shared helper.

That helper is `RemoteBearerToken.Read`, which both controllers use to pull the `oah_` token out of
the `Authorization` header. The six lines were duplicated at first; they were pulled out because
the check that the token starts with `oah_` is the only thing that stops a share link token or an
OAuth bearer from being looked up as a personal token, and a copy that drifted would lose it in
one controller without the other noticing. The route-table test already insists that every
`api/remote` action is `[AllowAnonymous]` and resolves its own token, so the new controller is
covered by the same invariant that caught the last wiring mistake.

## "Shared with me"

`SessionShareStore.ListSharedWithAsync` existed without a caller — the web app resolves shared
sessions one at a time by link or id and never lists them. It now backs `GET /api/sessions/shared`
and `GET /api/remote/sessions/shared`, which return `SharedSessionInfo[]`: the same sanitised shape
the shared-session page reads, with `accessRole` and `sharedBy` and without the owner-only fields
(MCP config, image, resources, callback token).

The task suggested `GET /api/remote/sessions?shared=true`. A query flag was rejected because it
would make one route answer with two different shapes, and the stdio client filters the plain
listing client-side on fields a `SharedSessionInfo` does not have. A path of its own
(`sessions/shared`) keeps each route to one response type. The literal segment wins over
`sessions/{id}` in ASP.NET routing, so no session id can shadow it. Without a licence the listing is
a 402 like every other sharing call, rather than an empty list that would look like "nobody has
shared anything with you".

## What the MCP tools deliberately leave out

- **The in-pod server (`agent-runtime/sessions`) does not get these tools.** A running agent
  should not be able to widen who can see or type into its own session; sharing is a decision the
  owner makes about the agent, not one the agent makes about itself.
- **No role update, no link update, no MCP policy.** Share, unshare, link, revoke and list cover
  what a script or an agent handing work to a person needs. Changing a role is unshare plus share;
  the MCP policy is a trust decision about tool access that belongs in the dialog where the
  configured servers are visible.
- **`role` and `expiresAt` arrive as strings**, for the reason `AgentHubMcpTools.ParseFlag`
  gives: a connected client caches the tool schema, and a typed parameter added later fails every
  call until it reconnects. An unknown role is reported (`invalid_role`) rather than defaulted, so
  a typo cannot quietly turn a Viewer grant into a Collaborator one.

## The dialog bug this uncovered

`ShareSessionDialog.vue` read `result.policy?.blockedServers` while the backend has always
returned `mcpPolicy`. The MCP-security textareas therefore came up empty on every open, and saving
the form with one restriction silently dropped the others. Fixed, and the vitest now seeds
`mcpPolicy` the way the backend spells it; the old test passed only because its mock used the same
wrong key as the component.
