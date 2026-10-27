# The session's API key is approved before Claude Code starts

An interactive Claude session in API-key mode stopped on the CLI's own question "Do you want to use
this API key?" with "No (recommended)" preselected. Nothing answered it: the session waited, and
Enter — also a task typed into the Workspace composer, whose Enter lands on the question — chose No
and left the CLI "Not logged in" (#313).

## What the entrypoint does

`claude/workspace-trust.mjs`, which already pre-accepts the workspace trust dialog in
`~/.claude.json`, also adds the key's last 20 characters to `customApiKeyResponses.approved` and
removes them from `rejected`. That is the CLI's own record of a "Yes" (Claude Code 2.1.287 identifies
an approved key by that suffix). The owner made the choice by selecting API-key mode for this session.

- Only the session's key: it is read from `ANTHROPIC_API_KEY`, which the pod spec sets only in API-key
  and Auto mode. Without it nothing is added. Entries a custom image put there are left alone.
- The key is read from the environment, not passed as an argument, where every process in the pod
  could read it from `/proc`. Only the suffix is written, and only to the CLI's own config.
- *Rejected: answer the question in the terminal* (press Up and Enter when it appears). That
  depends on the screen and its timing, and anything the person or the composer types first lands
  on the question; the config entry is read before the CLI draws anything.

## The CLI's backups of `~/.claude.json` leave the state archive

The proof found the suffix in the uploaded state archive: Claude Code keeps copies of
`~/.claude.json` in `~/.claude/backups/`, and `~/.claude` is what the archive carries. The archive
deliberately leaves `~/.claude.json` itself out, and the entrypoint rewrites it on every start, so a
backup is never read; the driver now excludes `.claude/backups` (`stateExcludes`).

On `main` the suffix also reached the uploaded scrollback, because the question displays it
(`sk-ant-...` plus the last 20 characters). With the question gone it is not drawn either.

## Proof

`scripts/proofs/313/api-key-start.cjs apikey` runs the shipped `claude/entrypoint.sh` and
`common/server.js` with the pinned Claude Code and a random fake key against a local Messages
endpoint (the #306 rig: only `/opt/session-agent`, `/tmp` and `/secrets` relocated). It types one
turn the way the composer does, waits for the periodic uploads, and searches them in memory.

| | `main` | this change |
|---|---|---|
| "Do you want to use this API key?" | shown | not shown |
| the turn | Enter chose No, "Not logged in", no request | answered |
| first request | — | `x-api-key` is the session's key, no `Authorization` |
| key in uploads, live transcript, entrypoint log | no | no |
| key suffix in uploads | yes (scrollback: the question shows it) | no |
