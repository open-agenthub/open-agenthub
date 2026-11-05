# Taking a session off the cluster, and giving it back

A session runs in a pod, and the conversation lives in that pod's `~/.claude`. Sometimes the pod is
the wrong place to be: the network the work needs is only reachable from a workstation, the files
want an IDE, or the laptop is going somewhere without a connection. The ask was therefore to
continue a hub session locally and later hand it back.

## The archive we already had

Nothing new had to be invented for the transport. A stopping pod already tars its agent home
directory and uploads it (`agent-runtime/common/server.js`, `persistState`), and a resuming pod
already downloads and unpacks it (`agent-runtime/common/entrypoint-common.sh`). That archive holds
`.claude/projects/<slug>/<sessionId>.jsonl` — exactly the file `claude --resume` reads.

So the API additions are deliberately dumb: `GET` and `PUT` of the raw archive on
`api/sessions/{id}/state` and `api/remote/sessions/{id}/state`.

**The alternative was a curated export** — a transcript format of our own, assembled from the
message store. It was rejected because the resume path already defines what a session's state is,
and a second definition would drift from it. The first divergence would show up as a session that
resumes in the hub but not locally, with nothing in either format saying which one was wrong.

Both surfaces exist because a personal API token only authenticates `api/remote`; `api/sessions`
needs an interactive login. A CLI on a laptop can only reach the former, the web app only the
latter, and a single endpoint would have locked out one of the two.

## Why pause and resume are mirrored onto the token surface

`api/remote` gained `pause` and `resume` as well, which looks like duplication of
`SessionsController`. It is not optional: a client that takes a conversation off the cluster has to
stop the pod first, and before this it could only do that in the web app. A transfer that needs a
browser click in the middle is not a transfer a skill can run.

Stopping is **not** about the archive going stale — the pod persists every 30 seconds
(`agent-runtime/common/server.js`), so a running session's state is at most that old. It is about
there being one conversation. A pod left running keeps answering and keeps writing the same key, so
the conversation forks, and the next upload silently resolves the fork by discarding one branch.

## Why an upload is refused while the pod is live

`ReplaceStateArchiveAsync` rejects a `Running` or `Pending` session with a `409`. The pod writes its
own state over the same key when it stops, so an upload accepted next to a live pod is discarded at
the next pause — and nothing in the response or the logs would say that the conversation someone
carried back had been thrown away. Failing loudly beforehand is the only version of this that is
honest. `SessionStatus.CanReplaceState` holds the rule, next to `CanPause` and `CanResume`, so it
stays testable without a cluster and cannot disagree with them.

## What the rewrite is, and why it is not a copy

Claude Code finds a session by the directory it was recorded in: the project folder is the working
directory with every character outside `[A-Za-z0-9]` replaced by a dash, and every transcript line
repeats that directory in a `cwd` field. `/workspace/repo` in the pod and `C:\Users\…\project` on a
workstation therefore disagree twice over, and a plain copy resumes nothing.

The first version left that rewrite to a separate Claude Code plugin,
[`agenthub-claude-plugin`](https://github.com/open-agenthub/agenthub-claude-plugin), on the grounds
that only the client knows the directory the session is going to. That is true, but the client can
say it. Since #453 the hub does the rewrite (`SessionLocalTransfer`) for a directory the caller
names, in both directions; see the next section for why.

Only `cwd` (and, on the way back, `sessionId`) changes, and only on lines that carry it. The
serializer keeps non-ASCII text and `<`, `>`, `&` unescaped, because a resuming pod compares the
restored transcript with the hub's copy byte for byte (#146): a rewrite that re-escaped every
umlaut would make the pod read its own history as "a different one". Claude Code's own records of
the move — an "Environment update: Primary working directory: … (was …)" attachment on resume —
are left alone. They are how the model learns it changed machines, and editing them would edit
history.

## Continuing locally without a token or a script (#453)

The state endpoints made a transfer possible; using them still meant creating a personal API token,
installing the plugin and running its script. The owner asked for this to be one step, from a
local Claude Code over MCP and from the web app. There are three surfaces, and one service
(`SessionTransferService`) behind all of them, so the rules are stated once:

| Surface | Credential | Who writes `~/.claude` |
|---|---|---|
| stdio MCP (`mcp/agenthub`): `session_take_local`, `session_hand_back` | personal API token | the stdio process — it runs on the person's machine |
| remote `/mcp`: the same two tools | OAuth sign-in | a `curl \| tar` command the caller runs |
| web app: **⇩ Continue locally** on the session page | the login | the same command, pasted into a terminal |

**Taking a session local** pauses it — only after an explicit `confirm`, because pausing interrupts
whatever the agent is doing — and then *waits until the pod is gone* before reading the archive. A
pause returns as soon as the pod has been told to stop; the pod writes its final state while it
shuts down, so an archive read straight after the pause misses exactly the turns the person just
watched. The same wait already guarded the runtime-image update (`WaitForPodGoneAsync`).

**Downloading** is refused while a pod is live, like uploading always was: a copy taken next to a
running pod is a fork, and the hand-back would later keep one branch. **Handing back** stores the
uploaded conversation under the session's own folder and id and resumes the session; it refuses an
upload that is not a continuation of the stored conversation (`transcript_unrelated`: the CLI only
appends, so a continuation still holds the first message), and anything in the upload but one
conversation's transcript and sidecar files, since those would land in the pod's home on resume.

### Why the hub rewrites instead of each client

The remote `/mcp` endpoint and the web app cannot run code on the workstation, so they can only hand
over something a stock shell can use. If the hub served the raw archive, that something would have
to be the rewrite — a script to download and run, or a dependency to install, which is the step the
issue asked to remove. With the rewrite in the hub, the whole local side is `curl` and `tar`, which
Linux, macOS and Windows 10+ all ship, and the archive it downloads is already laid out relative to
the home directory. The stdio server then uses the same endpoint rather than a second
implementation that could drift from it — the reason the curated export was rejected above.

### Why links, and why in Postgres

**The alternative was to accept the OAuth token or a session cookie in the command.** Pasting a
bearer token into a shell puts it in the history file, and the OAuth access token is the MCP
client's, not something to hand to `curl`. A transfer link instead grants exactly one transfer of
one session in one direction, for 15 minutes, and is removed by its first use
(`DELETE … RETURNING`, so two concurrent uses cannot both succeed). Every failure to redeem answers
the same 404, so a probe cannot tell an expired link from one that never existed.

**The alternative storage was a signed link** (Data Protection). It was rejected because single use
needs a record that the first redemption removes, and because the backend runs as several replicas:
a link minted by one has to be honoured by the other, and the database is the one store both share.
Only the SHA-256 of the link's secret is stored, as for API tokens.

The link's origin is `FrontendOrigin`, never the request's Host header (the same reasoning as
`SessionUrl`): the person's own shell sends their conversation to it. An instance without
`FrontendOrigin` answers `origin_not_configured`; the stdio server needs no link and still works.

### The commands

- `tar -xzkf`: `-k` keeps a transcript that is already on the machine. It may hold turns taken there
  since the last transfer; tar refusing, and stopping the `&&` chain, is the only warning there would
  be. The stdio server refuses the same case with `local_transcript_differs` unless told to overwrite.
- The hand-back packs the conversation's sidecar folder only if it exists; naming it unconditionally
  makes tar fail for every session that never ran a subagent.
- The PowerShell variant takes its temporary file from `[IO.Path]::GetTempPath()` and `Join-Path`,
  not `"$env:TEMP\…"`: `TEMP` is unset in PowerShell on macOS and Linux, and a backslash there is part
  of a file name. Found by running the commands under real `pwsh` (`tests/SessionTransferProof`).
- The stdio server writes below `CLAUDE_CONFIG_DIR` when it is set — files written to `~/.claude`
  regardless would sit where that CLI never looks. The pasted command cannot know that variable and
  always writes `~/.claude`.

## What this does not do

- **A session that never ran has nothing to transfer.** The hub stores state when a pod stops.
- **Claude Code only.** The other runtimes store a different home directory; the archive is
  per-provider (`claude-state.tgz`, `codex-state.tgz`, …) and only the Claude one holds something
  `--resume` can read.
- **Files are not part of it.** Session files live under their own keys and have their own API.
- **The repository is not part of it either.** The local directory should be a checkout of the same
  repository; otherwise the conversation refers to files that are not there. The hand-back moves the
  conversation only — commit and push code changes as usual.
