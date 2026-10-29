# Question tool proof (#343): questions from the question tool, "waiting" only while one is open

Real pinned CLIs (Claude Code 2.1.287, Codex 0.160.0, OpenCode 1.18.35) in the real
`common/server.js` under proot (runtime copy at `/opt/session-agent`, its own `/etc/codex` and
`/etc/opencode`), a mock model that asks each CLI's question tool on `ASK-ME`, a stand-in hub that
sets `questionPending` on `/notify` (not for an `idle: true` one) and clears it on `/turn-started` as
`InternalController` does (the controller itself is covered by `InternalNotifyTests`),
and Chromium over the Vite frontend.

Setup in a scratch dir `$RIG_HOME`: `npm i` the pinned CLIs into `clis/`, a static proot at
`proot/proot`, `chrome.env` with the rootless Chromium library path, then
`./build.sh <worktree>/agent-runtime $RIG_HOME/sa343` (and `samain` from a main worktree).

    RIG_HOME=... ./run343.sh ask|cancel|idle claude|codex|opencode sa343|samain <label>
    RIG_HOME=... ASK_FILE=$PWD/multi1.json ./run343.sh multi claude sa343 fixed
    APP=http://localhost:5174 ...   # a second Vite serving main's frontend for the baseline
    IDLE_S=80 NOTIFY_TRACE=1 ...    # idle scenario length; trace notify-hook.sh

`pty-rig.mjs` drives a CLI in a PTY without the session agent (screens and hook payloads:
`HOOKLOG`, `CODEX_ASK`, `OC_ASK`, `OC_PLUGIN`, `PROOT`/`PROOT_BINDS`).

The `idle` scenario is the "no question" proof: one prompt, then the real Claude Code's
`idle_prompt` notification about 60 s later. It reaches the hub as `{"event":"question","idle":true}`,
and neither `questionPending` nor the Workspace's waiting line may turn on.
Results land in `$RIG_HOME/result-*.json`, and screenshots in `$RIG_HOME/shots/`.
