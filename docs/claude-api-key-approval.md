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

`~/.claude.json` itself, which now holds the suffix, never was in the archive: `archiveScript` tars
only the driver's `stateDir` (`.claude`) from the home directory. The proof lists the uploaded
archive's entries: no `.claude.json`, and with this change no backups.

**Older archives contain more than the suffix.** Measured on `main` with the same rig:

- The backup written at every CLI start is the file the entrypoint just wrote: onboarding, the
  workspace trust, and `mcpServers` — the session's MCP server definitions **including their
  headers and env**. A fake `Authorization: Bearer` token of a session MCP server was found in the
  uploaded state archive. Every archive of a Claude session with a header- or env-authenticated MCP
  server holds those values.
- A second CLI start in the same home (a restart for resume or account switch, `claude` in the shell
  tab) backs up the live file: `oauthAccount` (account UUID, e-mail address, organization UUID),
  `userID` and `machineID`. A run with a fake login showed the e-mail in that backup.
- A key approved by hand ("Yes" on the question) would have put its suffix there the same way.

No login tokens: `.credentials.json` was excluded before and the backups are of `~/.claude.json`
only. This change stops new archives from carrying the backups; archives already uploaded keep them
until they are replaced or deleted.

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
