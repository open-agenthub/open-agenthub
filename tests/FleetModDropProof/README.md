# Fleet mod: a dropped submit keeps the message (#340)

Proof material for #340, kept so the next bump of Claude Code can rerun it. Nothing here runs in CI.

## `drop-retry.mjs`: the real interactive Claude Code

This is the claude-mod-real-cli-rig with one addition: a settings `UserPromptSubmit` hook that
blocks the first `DROPS` prompts (exit 2). Claude Code then answers the mod's `$.prompt.submit`
with `{ drop }`. The rig has a stand-in session agent on 127.0.0.1:17681 and a mock Messages API
on :17682, and runs the real CLI in a PTY via `script`. No login and no docker.

```bash
cp -r agent-runtime/claude/mods/agenthub-fleet mod
sed -i 's/127.0.0.1:7681/127.0.0.1:17681/' mod/hooks/register.js
mkdir run && cd run
DROPS=1 CLAUDE_BIN=<node_modules>/.bin/claude node ../drop-retry.mjs $PWD/../mod plain   # or priority, or "plain busy"
```

Exit 0 means the model received the fleet message.

Results from 2026-10-29. "main" is `register.js` before this fix.

| CLI | mod | message | drops | UserPromptSubmit calls | model saw it |
|---|---|---|---|---|---|
| 2.1.312 | main | plain | 1 | 1 | **no**: lost after the hub had marked it delivered |
| 2.1.312 | branch | plain | 1 | 2 | yes, once |
| 2.1.312 | main | priority | 1 | 1 | **no** |
| 2.1.312 | branch | priority | 1 | 2 | yes, once |
| 2.1.312 | branch | plain | 3 | 4 | yes, once |
| 2.1.312 | branch | plain | 0 | 1 | yes, once (control) |
| 2.1.312 | branch | plain, during a person's turn | 0 | 2 | yes, after the turn (a third request is Claude Code's prompt suggestion) |
| 2.1.287 | main | plain | 1 | 1 | **no** |
| 2.1.287 | branch | plain | 1 | 2 | yes, once |

The same scenario also ran through the real `common/server.js` with the fleet-delivery-real-cli-rig
on 2.1.312. Its `claudedrop` scenario sets the same blocking hook in `$HOME/.claude/settings.json`.
In both runs the session agent answered `queued-for-mod`, so the hub marked the message delivered.
On main the model never received it; on the branch it received it once. That rig's regular
`claude` scenario (idle, mid-turn and priority) passes as well.

## `probe-throwing-hook.test.ts`: what the test kit does with a bad hook

Put it in a plugin's `tests/` and run `claude plugin test`. On every version, a `prompt.submit`
hook that throws or returns something other than `{ text }` / `{ drop }` is skipped. The kit has
no engine beneath it, so `$.prompt.submit` then rejects with "no implementation for prompt.submit".
From 2.1.292 on, the kit also fails a test whose own hook returned such a result (bisected:
2.1.291 passes, 2.1.292 fails). The old fleet-mod test relied on that rejection. A valid refusal,
`{ drop }`, resolves on 2.1.287 through 2.1.312.

## `pr-step-refused-create.sh`: the nightly job's PR step

This runs the step's block from `update-agent-runtimes.yml` with the real `gh` for the lookup and a
refused `gh pr create`. Expected result: an `::error::` line, exit 1, and no edit of the merged #47.
