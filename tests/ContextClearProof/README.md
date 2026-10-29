# Context clear handover proof (#336)

After `context_clear`, a session could start its fresh conversation without the handover. These
scripts check whether the handover reaches that conversation, comparing two checkouts (main and
the branch). Pinned Claude Code or Codex runs in the PTY of the real `common/server.js`, under
static proot, with the shipped hooks and `codex/requirements.toml`:

| Piece | What runs |
|---|---|
| Runtime | `build.sh` lays out `/opt/session-agent` from an `agent-runtime` checkout (main or branch) |
| Session agent | `fault-server.cjs`: the real `common/server.js`, its PTY wrapped to inject one fault right after the clear command |
| CLI | the pinned Claude Code (`/usr/local/bin/claude`) or Codex (`<scratch>/clis`) |
| Model | a mock in `rig.cjs` that records requests (Messages API and Responses API) |
| Hub | a stand-in: policy allow, empty inbox; the clear request is POSTed to the pod's `/agenthub/context-clear`, as the backend forwards the tool call |

Faults (`FAULT=`): `drop` loses the first input after the clear; `busy` writes invisible output for
20 s after it; `restart` kills the CLI and the session agent 1 s after it, and a second process
resumes on the same home. Options: `QUEUED=1` sends an owner message while the clearing turn
runs, `REMINDER=1` makes the skill-reminder Stop hook continue that turn, and `MENU_ANSWER_MS=`
answers an open CLI menu with Enter and then sends an owner prompt.

## Run

```sh
S=<scratch dir outside the repository>
curl -fsSL -o $S/proot https://proot.gitlab.io/proot/bin/proot && chmod +x $S/proot
npm i --prefix $S/clis @openai/codex@<CODEX_VERSION from codex/Dockerfile>
(cd <checkout>/agent-runtime/session-agent && npm ci --ignore-scripts)
mkdir -p $S/rig && cp rig.cjs $S/rig/
R=$S/rig WT=<checkout>/agent-runtime ./build.sh     # again after every code change
cd $S/rig && R=$PWD AGENT=codex FAULT=drop node rig.cjs
```

The rig prints one `RESULT {...}` line. It contains: whether a model request carried the handover,
the seconds since the clear request, whether that request was free of the old task (a fresh
context), the first user turn of the newest native transcript, the order of model requests, and
the Workspace `handover` frames. The work directory is a git repository, which is what makes
Codex's `/new` ask "Where should the new conversation run?" (0.160.0 without `worktrees = false`,
0.165.0 always); the driver clears with `/clear`, which never asks. The fault proxy fires on the
clear command read from the built driver, so a main build that still types `/new` is faulted too.

## Following the new conversation, and a restart after it (#351)

`FOLLOW=1` (with `FAULT=none`) continues after the handover. It checks what the Workspace shows:

- the pod's live transcript (`/agenthub/transcript`) has the old turn, a `Conversation cleared` record naming the new thread's rollout, and the handover after it;
- the terminal stream carries `[agent] Conversation cleared.`;
- the backend's conversation page has the same entries.

It then sends a later turn, which must reach the model with the handover in context and without
the old task. `RESTART=1` adds a pod restart after that turn. The whole proot process group is
killed and `.codex/agenthub-active` is removed, because the state archive excludes it. A new
session agent then starts on the same home with `AGENTHUB_RESUME=1`. The rig records the thread
id the resumed `codex resume` was started with. One more turn must land in the new thread's
rollout and on the conversation page.

The backend page comes from `Host/`, a throwaway ASP.NET host. It runs the real
`LiveTranscriptReader` (pod requests, offset follow, `NativeTranscript` for Codex) and
`SessionTranscripts.PageAsync`, which is what `GET /api/sessions/{id}/conversation` runs for a
running session. Session storage and pod identity are fixtures:

```sh
dotnet build Host -c Release -o $S/host
cd $S/rig && R=$PWD AGENT=codex FAULT=none RESTART=1 HOST_DLL=$S/host/Host.dll node rig.cjs
```

Without `HOST_DLL` the backend checks are skipped and only the pod side is recorded.
