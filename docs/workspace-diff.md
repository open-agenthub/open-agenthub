# Workspace code review (#154)

The opt-in Changes companion pane reads Git in the session workspace through the common
session agent. It works for every provider and discovers repositories below /workspace,
including nested repositories and worktrees (.git files). Discovery never follows symlinks,
skips dependency and metadata directories, and reports when its bounded scan is incomplete.
No provider hook or transcript parsing is involved: those miss edits made by subprocesses.

The default comparison is the merge base of HEAD and the remote default branch (then
origin/main, main, HEAD when unavailable). A second comparison uses HEAD for uncommitted
edits. Both include staged, unstaged and non-ignored untracked files. This is a review of
current disk state, not attribution of edits to a particular actor. HEAD is not a historical
snapshot of the session: commits and branch switches change it. We rejected per-edit backup
copies because they add writes and retention policy to every provider and cannot reliably
observe atomic replacements. Git is read-only here; no fetch, index update, checkout or hooks.

Only an open, visible pane polls, every three seconds after the preceding request finishes.
This captures writes during a turn without provider-specific turn-end notifications. The
agent shares in-flight requests and caches each comparison for two seconds across viewers.
Recursive watchers were rejected because large dependency trees exhaust watch descriptors,
and new repositories and atomic renames require a second discovery mechanism anyway.
Hidden tabs stop requesting and reopening fetches current state. Closing the pane unmounts it.
Opt-in and comparison are local to the viewer; neither changes session presentation.

Each response caps discovery at 10,000 directories and depth 12, repositories at 32, changed
files at 200 across repositories, individual patches at 64 KiB and total patches at 512 KiB.
Git commands time out after three seconds and have bounded stdout. Binary files show a label;
large patches show an explicit omission. Truncated inventories are marked, never presented
as complete. No arbitrary path or ref is accepted from the viewer. Untracked symlinks are
omitted so Git cannot read their target. Ignored files are excluded by Git. Contents may
change between listing and patch read; the next poll reconciles that transient state.

The authenticated backend resolves session access on every request and forwards only to
the stored pod address with its callback token. Shared-link reads use the same read-only
resolver as Files, including licence, expiry and revocation checks. Owner, collaborator and
viewer can read; no role can mutate Git through this endpoint. No diff is retained by the
backend, browser storage or artifacts, and responses are marked no-store. A stopped session
or old runtime reports unavailability instead of showing a cached patch from another pod.

## Known limits

- **Pod identity.** The backend reads the diff from the pod address stored for the session and
  sends the session's callback token there, exactly as the file reader does. It does not first
  confirm that the address still belongs to that session's pod. The transcript reader gains that
  confirmation in #143; the diff and file readers should adopt the same check once it lands,
  rather than each growing its own.
- **Git configuration in the workspace.** The reader disables external diff drivers, textconv and
  fsmonitor, but `git diff` against the working tree still applies clean filters that an in-repo
  `.gitattributes` and the repository config name. Those are commands the agent could write. They
  run in the session-agent process, inside the same pod and as the same user as the agent, which
  can already run arbitrary commands there, so this grants nothing new. It would matter if the
  reader ever moved into a more privileged process than the agent; disabling filters then is part
  of that move.

## Verification

The repeatable runtime tests create real repositories and a linked worktree, then exercise
staged/unstaged/untracked changes, branch commits, deletions, binary and oversized content,
unborn HEAD, ignored files, discovery symlinks, the file-count ceiling and authenticated
TCP HTTP through common/server.js. Backend tests cover every file-reader role, per-request
revocation, invalid comparisons, stopped sessions, callback headers and oversized pod replies.
The pane tests cover escaping, omission notices, comparison changes, polling lifecycle and
late replies after navigation.

A throwaway running common session server and backend host were also connected to the real
Workspace component in the visible browser. A disk edit appeared on the next poll. The
1280px and 390px views rendered correctly, patch markup remained text, closing Changes and
switching to Agent on mobile stopped diff requests, and no browser errors occurred. The host
used the actual controllers, proxy and session access service with in-memory access records;
owner/viewer/collaborator, invalid links, revocation, licence loss and stopped-pod paths were
checked over HTTP. This did not exercise cluster scheduling or database-backed share expiry;
those remain covered by the existing access service/store suite and CI database checks.
