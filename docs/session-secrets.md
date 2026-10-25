# Named secrets for sessions: where they live, how they reach a pod, what keeps them out of view

Handing an agent a secret used to mean pasting it into the prompt or the chat, where it stays in
the transcript, the scrollback and every chat relay that posted the turn. The credential store only
knew fixed kinds (provider logins, API keys, git tokens, the SSH key). This file records how a
generic store for named secrets was added, and the alternatives each decision rejected.

## Storage: one Kubernetes secret per user, never mounted

A user's secrets live in `usersecrets-<owner>`: metadata (id, name, delivery, description, last
change) as one JSON document under `index`, and every value under a key of its own,
`value.<id>`, as raw bytes. `GET /api/secrets` answers with the metadata; nothing answers with a
value, and the response type has no field one could end up in.

- *Rejected: the values inside the JSON document*, as `GitPatStore` keeps its tokens. JSON escaping
  can grow a value with control characters to six times its size, and the 1 MiB a Kubernetes
  secret may hold would then be reached long before the limits below say it should be.
- *Rejected: the existing `creds-<owner>` secret.* It is projected into every pod (key by key, so
  safely today), and it already holds the SSH key and every PAT; a full secret store would have
  shared the 1 MiB with them.
- **Limits:** 32 secrets, 16 KiB each, a name of at most 64 characters. A full store is about half
  of what a Kubernetes secret may hold, so it can always still be written. 16 KiB fits a
  service-account key or a kubeconfig; anything bigger belongs in the workspace.
- **Writes are optimistic.** The store is read, changed and replaced with the `resourceVersion` it
  was read at, and retried with jitter on a conflict. A plain replace let the later of two writes
  drop the other's secret; the secret request flow writes here while the owner may be editing the
  same store in another tab. Against a real API server, eight concurrent writers exhausted five
  immediate retries, which is why there are sixteen, jittered.
- The secret carries the owner label, so the account purge removes it with the others.

## Names, and which ones are reserved

A secret is delivered either as an **environment variable** (`env`) or as a **file** in
`~/.secrets` (`file`); its name is the variable's or the file's name. The id identifies a secret,
not the name: `POST /api/secrets` without an `id` stores a new one, with an `id` it changes that
one — name, delivery, description, and the value if one is sent. The id survives every change,
rename included, so project and session selections keep pointing at it.

### Duplicate names, and where they are refused (#253)

Until #253 names were unique per user (case-insensitively), and saving under a stored name updated
that secret. That made a production and a staging `API_KEY` impossible to keep side by side, though
no session should ever get both. Names may now repeat. What must not repeat is **where a secret
lands in one session**: two secrets with the same delivery and the same name would be one key of
the session's copy, and the later one would silently win. The comparison is exact (`API_KEY` and
`api_key` are two variables and two files), and a variable and a file of one name do not collide.

`SessionSecretConflicts` refuses a set with such a pair wherever a session's set is decided, with
a 400 (an `invalid_argument` over MCP) that names the place and the ids:

| Where | Checked |
|---|---|
| creating a session (web, API, token, MCP spawn) | its selection, grants and inherited grants together |
| editing a session | the same, when secrets, grants or the project change |
| a project's selection (create, edit) | the selection itself — every later session would get both — and every session in it |
| saving a secret (rename, change of delivery) | every project and every session, inside the store write, so nothing is stored |
| answering a secret request | the session with the new grant |

The dialogs show the clash before saving and disable the button; the hub's check is what holds.
Delivery itself withholds both sides of a pair, should two concurrent edits ever produce one, and
logs the session id: an agent finding a variable missing is better than one working with a value the
owner did not mean.

- *Rejected: keep names unique and let the owner pick another name.* The name is what the tool
  reads (`KUBECONFIG`-style conventions); `API_KEY_STAGING` would need a wrapper in every session.
- *Rejected: let a grant override the selection on a clash.* Which one wins is exactly what the
  owner did not say; a refusal asks them.
- A request for a name with two stored namesakes opens a new form instead of one of them; the form
  lists the namesakes.

- Variables: `[A-Za-z_][A-Za-z0-9_]*`.
- Files: `[A-Za-z0-9_][A-Za-z0-9._-]*` — no leading dot, so neither `.`, `..` nor the kubelet's own
  `..data` link can be named, and no slash, so a file cannot leave the directory.

**Reserved variables** (`SessionSecretStore.ReservedEnvNames` and `ReservedEnvPrefixes`) are
refused at the edge:

| Reserved | Why |
|---|---|
| `AGENTHUB_*` | everything the runtime itself sets: callback token, presigned upload urls, mode, policy |
| `HOME`, `PATH`, `USER`, `LOGNAME`, `SHELL`, `TERM`, `PWD`, `OLDPWD`, `HOSTNAME`, `SHLVL`, `TMPDIR`, `TMP`, `TEMP`, `LANG`, `LANGUAGE`, `TZ`, `LC_*`, `XDG_*` | the process environment the runtime and the CLIs rely on; `TMPDIR` points at a directory the agent owns on purpose (CLAUDE.md) |
| `LD_*`, `DYLD_*`, `NODE_*`, `NPM_CONFIG_*`, `GIT_*`, `SSH_*`, `IFS`, `ENV`, `BASH_ENV`, `CDPATH`, `PROMPT_COMMAND`, `PS1`–`PS4` | they change which code a process loads. The session agent (PID 1, holding the callback token) and the approval hooks are such processes, and the hooks are what an interactive session's tool approvals rest on |
| `ANTHROPIC_*`, `OPENAI_*`, `CLAUDE_*`, `CODEX_*`, `CURSOR_*`, `OPENCODE_*`, `OPENCLAW_*` | the runtime sets the API keys among them; the rest configure the CLIs, and a variable such as `ANTHROPIC_BASE_URL` redirects where the CLI sends the login it was given. A secret request is exactly where an agent could ask for one |
| `OTEL_*`, `KUBERNETES_*` | telemetry the runtime configures; service discovery variables |

A test builds the pod spec for every agent and authentication mode and fails if any variable in it
is not reserved, so a variable added to the pod later cannot be overridden just because nobody
remembered this list. The price of reserving whole provider prefixes: a tool that wants its own
`OPENAI_API_KEY` gets the secret under another name or as a file.

## Selection: projects select, sessions narrow

A project holds `secretIds`; a session holds `secretIds` too.

| Session's `secretIds` | In a project | Outside a project |
|---|---|---|
| `null` (omitted) | the project's selection, live | nothing |
| `["id", …]` | these, as far as the project still selects them | exactly these |
| `["*"]` | spelling of `null`, so a PATCH can go back to following the project | |

- **The default is nothing.** For git PATs `null` means "the default PAT of every host", because a PAT is only
  ever offered to the host it belongs to. A secret is handed to the agent outright, so
  "everything, including what is stored later" is not a default anyone should get unasked.
- **An explicit list must lie within the project.** It is refused (400), not cut down: a caller
  that asked for a secret and silently got a session without it finds out only when the agent fails
  on the missing variable. A project narrowed *later* does take the secret away from its sessions,
  running ones included — a selection can lose entries that way, never gain them.
- *Rejected: the project as an allow list only*, with every session choosing its own. Then a new
  session in a project would start with nothing, and the project page would be a list of
  permissions nobody uses by default. The issue asked for "a session created in the project
  receives that selection"; the allow list falls out of it.
- A duplicate keeps the source's list when it stays in the same project and follows the new
  project otherwise. Scheduled sessions refuse a change of `secretIds` like any other runtime
  field, because their CronJob template fixes the variable names.

## Delivery into the pod

At every spawn the backend writes two secrets for the session from the store and the selection:

- `sessionsecrets-<id>` holds the **files**. It is mounted whole at `$HOME/.secrets`, read-only,
  without `items` or `subPath`. That is what lets the kubelet replace a changed file, add a new one
  or remove one in a running pod (within its sync period, about a minute). It is created, possibly
  empty, for every session, so a secret allowed while the session runs appears without a restart.
- `sessionenv-<id>` holds the **variables**, referenced one by one from the agent container's `env`
  with `secretKeyRef` (optional, so a variable removed in between leaves it unset rather than the
  container stuck in `CreateContainerConfigError`). Variables are fixed when the container starts:
  a changed or added variable reaches the session at its next start, and a removed one stays in the
  running process until then. Pause the session to revoke one at once.
- Only the agent container gets them; the git clone init container and the helper containers of
  docs/session-containers.md get none. `AGENTHUB_SECRETS_DIR` names the directory and
  `AGENTHUB_SECRET_ENV` the variables (names only).
- Whenever a secret, a project's selection or a session's selection changes, the copies of every
  affected session that has ever been spawned are rewritten (`SyncSessionSecretsAsync`). A session
  that never ran gets them at its first start.
  A copy whose session row is gone (a delete that failed half-way) is removed by the same pass.

*Rejected: mounting the user's store into the pod.* A session that should see one secret could
then list the others in the mounted directory. *Rejected: one per-session secret for both kinds,*
mounted for the files: every variable would have been on disk a second time. *Rejected: the
entrypoint exporting the files as variables*: a custom image runs our entrypoint too, but the
values would then sit in PID 1 just the same, with an extra copy step and no live update either.

## Keeping values out of what other people read

| Where | What happens |
|---|---|
| API and listings (`/api/secrets`, `/api/remote/credentials`) | names, delivery, description only |
| Logs | the backend logs the secret id; never a name or a value |
| State archive | only each driver's state directory is archived (docs/state-archive.md); `~/.secrets` is outside every one |
| Files pane | `upload_file` only accepts files under the workspace or the managed output root after `realpath`, so `~/.secrets` and any symlink into it are refused (`files-paths.test.js`) |
| Scrollback and transcript uploads | the session agent masks every value in what it uploads (`common/secret-mask.js`): the scrollback buffer itself once per persistence tick — so a value the PTY delivered in two chunks is caught and a reconnecting client is replayed the masked text — and the transcript on its way to the hub and to object storage, the latter through curl's stdin rather than a second copy in the shared `/tmp` |
| Chat relays | the backend masks the `notify` message with the values in the session's own copies (`SecretMask`), because Codex's and Cursor's turn hooks send the agent's last message |

Masking replaces a value, its JSON-escaped form and each line of a multi-line value with
`[secret:NAME]`. Values shorter than eight characters are not masked: "1234" would be replaced in
every number and still give the value away by where the gaps are. Verified with the real
`common/server.js`, real curl and a kubelet-style `..data` mount: on main all four uploads carried
the file value, a PEM and an environment value; with the masking none of them did.

- **The CLI's own transcript is not changed**, only the text returned or uploaded. The agent resumes
  from that file, and a replaced value there would be gone from its memory of the conversation.
  The state archive therefore still holds whatever the agent itself printed; it is uploaded to the
  owner's own object storage prefix and read only by a resume.
- **History is masked before delivery**, including a WebSocket scrollback replay before the first
  persistence tick and the pod's live transcript endpoint. The endpoint refreshes projected file
  values on each read. Its size and next-offset headers count the original file's UTF-8 bytes;
  replacement markers can have different lengths, so counting the response bytes would skip or
  repeat records. Expanded responses still fit the backend's 8 MiB limit, ending at a whole record.
- Masking the pod's history outputs once covers the Workspace live follow and conversation API;
  persisted copies feed share links, remote transcript reads and MCP transcript/log tools. Chat
  notifications retain the backend's session-secret mask because hooks can bypass this transport.
  Rejected: masking only the Workspace controller, which leaves the other readers exposed, and
  rewriting the native file, which changes what the agent resumes from.
- **The live terminal is not masked.** The person watching it is the owner or someone the owner
  gave terminal input, and either can read `~/.secrets` from that terminal anyway. A collaborator
  with terminal input can read everything the pod holds; the sharing dialog has to say so.
- An agent can still copy a value into the workspace and upload that file, or send it anywhere its
  network policy allows. Secrets are for an agent the owner trusts with them; the masking keeps
  them out of the records, it does not make the agent unable to use them.

## Token scopes and spawning

`api_tokens.allowed_credentials` gains a `secrets` part, in the format of docs/credential-scopes.md:

```json
{ "providerAccounts": { "claude": ["*"] }, "gitPats": ["*"], "secrets": ["3f9a1c2b7e0d4a61"] }
```

An allow list like every other part: missing means none, so a token restricted before secrets
existed hands out none. `["*"]` allows every secret, including ones stored later. A restricted
token creating a session:

- names secrets → each must be allowed, otherwise `403 secret_not_allowed`. The names are checked
  before any id is looked up, so the token learns nothing about ids it may not use;
- names none → gets the allowed part of the project's selection, written out as an explicit list.
  Left at `null` the session would follow a project selection the token never saw.

`GET /api/remote/credentials` lists the allowed secrets by id, name and delivery.

**A session spawned over MCP carries at most what its parent has.** Without a list the child
inherits its parent's ordinary selection and grants enabled for inheritance, again as an explicit list (`null` would follow the
project, which may select more than the parent was narrowed to); a child placed in another project
keeps only what both select. Asking for a secret the parent does not have is `403
secret_not_allowed` — refused rather than dropped, so the agent learns why the child lacks it.

## Asking for a secret: `secret_request`

An agent that needs a secret it does not have calls `secret_request` on `agenthub_sessions` with
a name, `env` or `file`, and what it is for. It does not ask in the conversation, where the answer
would be recorded for good.

1. The backend checks the name against the same rules as a stored secret (reserved variables
   included). If the session already has a secret of that name, the call is answered at once.
   Otherwise a request is opened (`secret_requests`, Postgres, because the tool polls through one
   replica while the owner answers through another).
2. Opening it is the pending-question path. The session is flagged as waiting, which is the badge,
   and every chat relay posts it as a `question` with a link to
   `<FrontendOrigin>/settings/secrets?request=<id>`. The agent's purpose text goes into that message
   as one bounded line, so it cannot add lines that look like the hub's own. In the web app the
   session shows the request next to its permission prompts.
3. The owner opens the link. The form is pre-filled with the name, delivery and purpose, or opens the
   stored secret of that name, whose value can be kept. Saving stores the value, then grants the
   secret to this one session. The link is taken off the address afterwards.
4. The tool polls until the request is answered or `waitMinutes` (default 10, at most 30) have
   passed; on a timeout it closes the request itself. It returns `provided`, with the name, the
   delivery and where to find the secret, or `not provided` for a refusal or a timeout. A file is
   waited for until the kubelet has put it into the pod. A variable is set from the next start, and
   the Settings page offers the owner a restart (pause, then resume) in two steps. Neither the hub nor
   the tool ever handles the value on the agent's side; the tool does not even read the file.

**A grant is per session and kept apart from the selection.** `sessions.secret_grant_ids` adds to
whatever the selection gives, even beyond the project. The owner answered this session's own
request, and widening the project for every session would give it to agents that never asked. The
edit dialog's compatibility field `secretGrantIds` only revokes. The owner can now replace
`secretGrants` explicitly, as described below.

**Asking cannot widen a limit.** A session created by a restricted token, or spawned by another
session, records a ceiling at creation (`sessions.secret_ceiling`): the token's secrets, or the
parent's at the moment of the spawn. A grant outside it is `403 secret_not_allowed`. The field is
set by the hub only and is ignored in a request body; otherwise the caller could lift its own limit.

- *Rejected: the backend handing the value to the tool.* It would put the value into the MCP
  result and from there into the transcript, the one place the whole flow exists to avoid.
- *Rejected: a value field in the chat relay.* The relays post into channels that outlive the
  session and are read by others; they carry the link, nothing else.
- *Rejected: growing the project's selection on a grant.* See above; the owner can still add the
  secret to the project afterwards if every session there should have it.
- In an interactive session a built-in MCP tool goes through the usual tool approval first, so the
  owner may see an approval prompt for `secret_request` before the secret question itself.
- At most five requests of a session can be open at once, and asking again for an open name waits
  on the same request; an agent in a loop cannot flood the relays.

Verified end to end in this pod. The real `agenthub_sessions` server ran over stdio with a real
session's callback token against the real backend (real Postgres, the API server rig), and the
owner answered in real Chromium through the banner and the pre-filled form. A watcher stood in for
the kubelet, copying the session's secret into the directory.
- The request was open and the session flagged as waiting; the link opened the pre-filled form.
- The tool returned `provided` with the file's path once the file was there, and no value. The file
  held the value typed in the browser.
- `secrets_list` showed it; the waiting flag was cleared; the grant was recorded.
- A refusal came back as `not provided`; a reserved name never reached the owner; no response the
  browser received carried the value.

## The web app

- **Settings → Secrets** is a tab of its own rather than a card in Credentials. Credentials are
  logins the runtime knows how to use; a secret is something the owner hands the agent, with a name
  and a purpose the agent sees. A value field is never filled from the store, an edit without a value
  keeps the stored one, and the typed value is dropped from the page once saved. Since #253 the
  name can be edited: the edit carries the id, so a renamed secret stays selected wherever it was.
  Before, the name was the identity, and a rename would have been a new secret that no project or
  session selected.
- **The project selection is a dialog on the project in the sidebar** (the key button next to
  rename and color), the same place every other project setting is changed. The session dialogs
  show the picker under the project field, because what they offer depends on it: the project's
  selection inside a project, every stored secret outside one, and the hint says which applies.
- **The token scope editor** lists secrets like git tokens. A token that ticks none is stored
  without a `secrets` part, exactly like a token restricted before secrets existed.
- **The share dialog says** that anyone with terminal input can read the session's secrets: the
  masking covers what is stored and relayed, not a live terminal.

Verified in real Chromium against the real backend (auth-off development mode, real Postgres, the
API server rig): a file secret typed in Settings reached the `sessionsecrets` copy of a session
created in the browser, the unticked variable did not, the project and token scope stored the
ticked ids, and no page and no API response carried a value.

## What needs a real pod

Verified against a real kube-apiserver with the chart's RBAC, as the backend's service account, and
real Postgres: the store and its concurrent writes, the pod being admitted under the namespace's
Pod Security level, both copies, live rewrites on rotate, narrow and delete, the masked relay text,
no value or name in any log line, and the account purge. What only a kubelet shows — files
appearing and changing in `~/.secrets` of a running pod, the variables in the agent's environment —
was not verifiable in a session pod.

## Found on the way

`DeleteUserSecretsAsync` (the Kubernetes part of the account purge) used `deletecollection`, which
the chart's role does not grant. Against the real API server the call was refused with 403 and the
purge stopped before it reached object storage. It now lists by the owner label and deletes one by
one with the verbs the role has.

## Explicit session grants (#209)

The owner can now set `secretGrants: [{ secretId, inheritToChildren }]` alongside `secretIds`
when creating or editing a session. The list replaces the grants; `[]` revokes them all and
omitting it leaves them unchanged on edit. It does not widen the project selection or change
what an explicit `secretIds` list may select. The older `secretGrantIds` edit field remains a
revoke-only compatibility path. A duplicate starts without grants unless its owner explicitly
chooses them in the duplicate dialog.

**Inheritance defaults to false.** Giving a credential to one agent must not silently give it to
agents that it starts later. This applies to existing request grants too. The persisted grant
column accepts its old string-array form and reads it as `requested`, non-inheritable grants;
new writes store the id, inheritance flag and service-assigned origin. No database rewrite or
additional copy of a value is needed. Inherited grants additionally record their source session;
clients cannot supply that link. The editor can explicitly make an inherited grant direct.
Saving a changed grant list is an owner approval of its entries; other edits omit that list.

A spawn receives the parent's effective project/session selection plus grants marked for
inheritance, narrowed by an explicit child list if supplied. An inheritable grant stays a grant
when the child joins a different project, with origin `inherited`; its inheritance flag remains
set for further descendants. A grant with inheritance disabled cannot be requested by id to
bypass that choice. Every child still records an immutable ceiling contained in what its parent
currently holds. Delivery intersects this ceiling too, so moving the child to another project or
changing a project's selection later cannot widen it. Revoking a parent's grant removes inherited descendant grants and rewrites their copies live.
A grant the owner gives the child directly is independent and survives the parent's revocation.
Disabling inheritance or deleting the parent also ends inherited access. Revoked inherited grants
are removed even from unstarted sessions so regranting a parent cannot resurrect old child access.

Restricted token creation and grant updates check every grant id against the token's secret
scope before looking up secrets. Existing session ceilings also constrain owner grant updates
and request answers. The agent's spawn endpoint rejects owner-grant fields; it only attaches
inherited grants computed by the service. A client cannot supply provenance or a ceiling.

`secrets_list` reports `provenance` (`project`, `granted`, `inherited`, `requested`) and
`inheritToChildren` without values. The session view shows the same origin for effective secrets;
the owner dialogs separately edit project selection and session grants. `project` labels the
ordinary selection path, including explicitly selected secrets outside a project. When a secret
is both selected and granted, the grant origin is shown; removing that grant leaves the ordinary
selection in effect. Grant changes refresh the session's copies through the existing delivery
path. Files update live; environment variables require the next start. Scheduled sessions can
choose grants at creation, but changes are rejected like other runtime fields because the
CronJob fixes variable names. Delete and recreate one to change grants.

Rejected alternatives:

- Widening the project selection would distribute a session-specific credential to unrelated
  agents, and would defeat revocation for this one session.
- Treating grants as explicit selection entries would either violate project validation or lose
  the grant when its project changes. Separate grant metadata preserves both rules.

### One control per grant (#233)

Each stored secret has one three-way switch — *Not allowed* · *This session* · *Also child
sessions* — in the new, edit and duplicate dialogs and in the session view's Secrets panel. It is a
`radiogroup` (arrow keys, Home/End, one tab stop per secret) in the look of the session bar's
Workspace | Terminal | Shell switch. *Not allowed* is the revoke; a new grant starts as *This
session*, so inheritance still has to be chosen. The session view saves each choice at once with a
PATCH that carries only `secretGrants`, puts the previous choice back when the hub refuses it, and
stays read-only for a scheduled session and for anyone who cannot manage the session. A secret the
project selects but nobody granted is labelled `project`, so *Not allowed* on its row is not read
as "the agent cannot see it".

- *Rejected: the grant checkbox plus an "Inherit to children" checkbox.* The second box appeared
  only once the first was ticked, so the row moved under the pointer, and the state a child would
  get was split across two controls. Three named states are the only valid combinations.
- *Rejected: editing grants in the session view behind a Save button.* A grant reaches the running
  pod's files at once; a pending, unsaved choice there would be one more way to believe a secret
  was revoked when it was not.
- Inheritance on by default would silently expand access, including for old request grants.
- Letting child agents submit their own grants or treating token grants as unscoped metadata
  would make the grant list an escape from existing credential limits.
- Independent copies for inherited grants would leave credentials in unattended descendants after
  their parent was revoked. Inherited grants retain a service-assigned source-session link; only
  an explicit owner grant on the child is independent. Replacing a child grant list deliberately
  approves its listed grants as direct owner grants; omitted lists retain inherited provenance.


The #209 real-service proof uses actual session, project, token and callback controllers with
Postgres and an isolated API server. It verifies owner grants beyond project selection, spawning
with inheritance off/on, explicit-id bypass denial, cross-project grants, immutable ceilings,
restricted tokens at creation/update, owner isolation and scheduled-session rejection. Admitted
pods have the expected Secret copies. Rotation rewrites those copies; parent revocation removes
inherited child and grandchild copies live, while a directly approved child keeps its grant.
Regranting the parent does not resurrect revoked children; disabling inheritance also revokes.
The rig has no kubelet, so projected-file timing and the running process environment are not
established by this proof. Files follow within the kubelet sync period; variables still require
restarting or pausing the process to revoke immediately.
