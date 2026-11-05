# The state archive: when a session's state directory is uploaded

A resumed session runs in a fresh pod. What lets the agent CLI continue its conversation there is
its state directory (`~/.claude`, `~/.codex`, …), which the session agent
(`agent-runtime/common/server.js`) archives and uploads to object storage, and which the
entrypoint (`agent-runtime/common/entrypoint-common.sh`) downloads and unpacks before the agent
starts. This file records when that archive is written and why; the commands themselves live in
`agent-runtime/common/state-archive.js`.

## What it used to cost

Until 0.13.0 the agent ran `tar czf` over the whole directory on every 30-second persistence
tick and uploaded the result, changed or not. Measured in a running Claude session — 33 MB in
1054 files, pod limited to 2 CPUs — that was 1.42 s of CPU and 11.1 MB uploaded per tick, around
the clock, idle sessions included. 27 of the 33 MB were installed plugins and skills that never
change between two ticks. The run is niced, so under load it stretched to many seconds.

## Four changes, and what each was weighed against

**1. Nothing is archived while nothing changed.** Before archiving, the agent takes a digest
over the name, type, size, mtime and ctime of every file the archive would contain
(`find … -printf | sort | sha256sum`) and compares it with the digest of the last *successful*
upload. Equal means skip. The listing opens no file: 0.02 s of CPU for 600 files.

- *Rejected: tar's own verbose listing* (`tar -cvvf /dev/null`), which would have shared the
  exclude matching with the archive for free. It prints timestamps to the second, `--full-time` and the
  pax format notwithstanding (GNU tar 1.34). A file rewritten to the same size within the second of the previous run — an
  SQLite database does exactly that — would look unchanged from then on.
- *Rejected: hashing the content* (`tar cf - | sha256sum`). Correct, but it reads every byte on
  every check, which is the cost this was meant to remove.
- *Rejected: newest mtime plus count plus total size.* A deleted-and-recreated or renamed file
  can leave all three as they were.
- The excludes are translated to `find -path`, which anchors a pattern that tar matches anywhere
  in a path. find can therefore only see *more* than tar archives: the error is an extra archive,
  never a missed one.
- The digest is taken before the archive and remembered only after the upload succeeded. A file
  written while tar runs changes the next digest; a failed upload leaves the old one in place and
  is retried on the next tick.
- If the digest cannot be taken (no `find`, a timeout, an empty answer), the agent archives.

**2. The state is archived at most every fourth tick — two minutes.** Scrollback and transcript
stay on the 30-second tick: they are small, uploaded on their own, and are what a person reads.
The state directory is only read by a resume. Once the two minutes have passed, the digest is
checked on every tick, so the first change after a quiet period is uploaded within 30 seconds.

- The price was what a pod loses when it dies *without* its SIGTERM (node failure, OOM kill): up
  to two minutes of conversation instead of 30 seconds. This note used to say the transcript the
  hub shows was not affected. It was: the resumed pod served and uploaded the older file, and the
  Workspace dropped those turns (#146). The resuming pod now appends the turns its archive
  missed from the hub's transcript before the agent starts (`docs/transcripts.md`), so a
  provider with a transcript loses at most the 30 seconds since the last upload. A longer
  interval was not chosen because with changes 1, 3 and 4 the remaining cost no longer
  justifies a larger window.
- The run on agent exit and on SIGTERM is not subject to the interval **or** the digest. It
  always archives: it is the last chance, and it must not depend on the change detection being
  right. It also got faster, which matters because a paused session's pod gets
  `PauseGracePeriodSeconds` (30 s by default) between SIGTERM and SIGKILL.
- A failed upload does not start the interval; the next tick tries again.

**3. Content the runtime gets back on its own is left out, per driver.** For Claude that is
`.claude/plugins/marketplaces`, the CLI's checkout of its plugin marketplace: 16 of 18 MB in the
session this was measured in. Verified by unpacking an archive without it into an empty home and
starting the CLI there: the directory was fetched again during start, because
`plugins/known_marketplaces.json` — which stays in the archive — names it.

- *Kept: `.claude/skills`.* Library skills are written again by the entrypoint
  (`common/skills.js`), but a skill created inside the session exists only in this directory
  until the upward sync has imported it, and a failed library fetch on resume would leave the
  session without the skills it had. Not worth the megabytes now that an unchanged directory
  costs nothing.
- *Kept: `.claude/plugins/cache` and `installed_plugins.json`.* Nothing was shown to bring back
  a plugin the user installed.
- The other drivers' lists are unchanged; theirs were measured when they were written.

**4. gzip level 1 instead of the default 6.** On the 18 MB directory: 0.43 s instead of 0.70 s of
CPU for 8.7 instead of 8.1 MB. The archive is still plain gzip, so the `tar xzf` of any older
entrypoint restores it and this entrypoint restores any older archive — the restore command did
not change at all, and `test/state-archive.test.js` runs it against both.

- *Rejected: another format (zstd, lz4).* Faster at the same ratio, but the tool is not in the
  runtime image this was measured in, a custom image would have to add it, and an older runtime's `tar xzf`
  cannot read it — a session resumed after a rollback would start without its conversation.
- *Rejected: no compression.* 0.07 s of CPU, but three times the upload on every archive.
- gzip is run through `tar -I 'gzip -1'`, not a pipe. A pipe reports the status of its last
  command, and the status of tar is what decides whether the archive may replace the last good
  one (0 and 1 yes — 1 is "a file changed while it was read", normal for a live transcript).

## What it costs now

The real persistence loop of `common/server.js`, driven for 20 ticks (ten minutes) against a copy
of a live Claude state directory (18 MB, 585 files) and a local upload endpoint; CPU is the whole
process tree, including about 0.1 s of start-up:

| | CPU before | uploaded before | CPU after | uploaded after |
|---|---|---|---|---|
| idle (nothing changes) | 15.0 s | 162 MB in 20 uploads | 0.37 s | 0.56 MB in 1 upload |
| active (transcript grows every tick) | 14.5 s | 162 MB in 20 uploads | 0.46 s | 2.8 MB in 5 uploads |

## What did not change

One run at a time, so archives cannot pile up; the side file, so an aborted run never replaces
the last good archive; tar's exit status 1 counting as success; the credential file staying out
of the archive (and now out of the digest, so a token refresh alone does not cause an upload).

## Generated MCP client configs stay out of the archive (#446)

Three runtimes wrote the session's generated MCP client configuration into their own state
directory, so every archive carried the MCP servers' literal header and env values — API keys,
gateway tokens, and with the skills proxy switched off the hub's `X-Agent-Token` — into object
storage, although the next start regenerates the configuration from the session's MCP document
anyway. Claude (`~/.claude.json`, `/tmp/agenthub-mcp.json`) and OpenCode (`~/.config/opencode`)
write outside their state directories and were not affected.

| Runtime | File | Treatment |
|---|---|---|
| Codex | `.codex/config.toml` | `stateExcludes` (`config.toml*`) |
| Cursor | `.cursor/mcp.json` | `stateExcludes` (`mcp.json*`) |
| OpenClaw | `.openclaw/openclaw.json` | archived, but holds references instead of values; its rotated `.bak*` copies are excluded |

**Codex and Cursor: excluded.** Both files are rewritten at every start (`config.toml` with `>`;
`mcp.json` whenever any MCP server or builtin is on, which is always, because the shared
entrypoint always enables the files server), so nothing written into them during a session ever
survived a restart. Excluding them loses nothing a resume used. The globs and a matching `rm` at start also cover
copies of either file: the pinned Codex keeps none (checked by having it rewrite its config with
`codex mcp add`), Cursor's CLI was not checked, and an archive written before this change could
carry one either way.

**OpenClaw keeps backups.** The real CLI rotates `openclaw.json.bak` through `.bak.4` on every
config write, and the entrypoint alone writes the config several times per start, so on `main` a
fresh session's archive held the callback-protected header four times and a resumed one six. The
copies are excluded and deleted at start; nothing reads them on a resume.

**OpenClaw: references, not an exclude.** `openclaw.json` also holds what a resume has to bring
back: the servers the agent added itself with `openclaw mcp add` (kept on purpose, see
`docs/api-created-sessions.md`) and the agent's own settings. Excluding the file would drop those;
stripping only the MCP part at archive time would mean rewriting a file the CLI may be writing at
that moment. Instead `openclaw/mcp-config.js` writes each literal env and header value of the
session's servers as a `${AGENTHUB_MCP_VALUE_<n>}` reference — OpenClaw interpolates those from its
environment, as it already did for the callback token — and the values into
`/tmp/agenthub-openclaw-mcp.env`, which the entrypoint exports before the first `openclaw` call.
Verified with the pinned CLI: the hub's `X-Agent-Token` reached a stand-in that refuses requests
without it, the archive contained the value zero times, and after a resume from that archive the
agent's own server was still listed (`tests/McpConfigStateProof`).

Limits:

- A server the agent or a person adds inside the session with `openclaw mcp add`/`set` is written
  by OpenClaw itself, as given. A literal value in it is archived like any other file content.
- A secret in a server's URL (a query parameter) is not moved; OpenClaw is not known to
  interpolate URLs. Use a header or `bearerTokenEnvVar` for credentials.
- An `openclaw` command run without the session agent's environment (for example from a shell that
  did not inherit it) warns `missing env var "AGENTHUB_MCP_VALUE_<n>"` and leaves that header out.

The values are still masked in uploads (#434): they are in `AGENTHUB_MCP_CONFIG`, which the mask
reads, and the mask now also takes every `AGENTHUB_MCP_VALUE_*` variable.

Existing archives still hold the old files until the session next saves: the restore brings them
back, the entrypoint rewrites them (Codex, Cursor) or replaces the managed entries with references
(OpenClaw), and the next archive contains no value.
