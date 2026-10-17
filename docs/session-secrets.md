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
`~/.secrets` (`file`); its name is the variable's or the file's name. Names are unique per user,
compared case-insensitively — `API_KEY` and `api_key` side by side would be two secrets nobody
reading the list can tell apart. Updating a secret under the same name keeps its id, so project and
session selections keep pointing at it; an update without a value keeps the stored one.

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

- **The default is nothing.** For git PATs `null` means "every stored PAT", because a PAT is only
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
| Scrollback and transcript uploads | the session agent masks every value in what it uploads: the scrollback to S3 and to the hub, the transcript to S3 and to the hub (`common/secret-mask.js`) |
| Chat relays | the backend masks the `notify` message with the values in the session's own copies (`SecretMask`), because Codex's and Cursor's turn hooks send the agent's last message |

Masking replaces a value, its JSON-escaped form and each line of a multi-line value with
`[secret:NAME]`. Values shorter than eight characters are not masked: "1234" would be replaced in
every number and still give the value away by where the gaps are. Verified with the real
`common/server.js`, real curl and a kubelet-style `..data` mount: on main all four uploads carried
the file value, a PEM and an environment value; with the masking none of them did.

- **The CLI's own transcript is not changed**, only the copy that is uploaded. The agent resumes
  from that file, and a replaced value there would be gone from its memory of the conversation.
  The state archive therefore still holds whatever the agent itself printed; it is uploaded to the
  owner's own object storage prefix and read only by a resume.
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
inherits exactly the parent's current set, again as an explicit list (`null` would follow the
project, which may select more than the parent was narrowed to); a child placed in another project
keeps only what both select. Asking for a secret the parent does not have is `403
secret_not_allowed` — refused rather than dropped, so the agent learns why the child lacks it.

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
