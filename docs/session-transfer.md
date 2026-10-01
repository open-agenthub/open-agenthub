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

## What the client has to do, and why it is not a copy

Claude Code finds a session by the directory it was recorded in: the project folder is the working
directory with every character outside `[A-Za-z0-9]` replaced by a dash, and every transcript line
repeats that directory in a `cwd` field. `/workspace/repo` in the pod and `C:\Users\…\project` on a
workstation therefore disagree twice over, and a plain copy resumes nothing.

That rewrite is client work, not hub work: only the client knows the directory the session is going
to. It lives in a separate Claude Code plugin,
[`agenthub-claude-plugin`](https://github.com/open-agenthub/agenthub-claude-plugin), rather than in
this repository — it runs on a workstation, releases on its own schedule, and has no reason to move
with `image.tag`.

## What this does not do

- **A session that never ran has nothing to transfer.** The hub stores state when a pod stops.
- **Claude Code only.** The other runtimes store a different home directory; the archive is
  per-provider (`claude-state.tgz`, `codex-state.tgz`, …) and only the Claude one holds something
  `--resume` can read.
- **Files are not part of it.** Session files live under their own keys and have their own API.
