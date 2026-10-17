# Skill library sync: how library skills reach a running agent

Sessions save skills to the library diligently: the end-of-turn reminder asks every turn. They
rarely read them through the skill-library MCP, though. An agent uses a skill when its CLI lists it
from the skill directory, because the listing puts every skill's description in context and the
CLI loads the skill when a task matches. A search tool has to be thought of first, and given the
right words. Until #108 the library was written into `~/.claude/skills` once, at pod start, and
only for Claude Code and OpenCode. So a skill saved by another session stayed invisible until a
restart, and Codex, Cursor and OpenClaw never saw any.

This file records how the directory is kept current while a session runs, and what each agent CLI
does with it. The code is `agent-runtime/common/skills.js` (pull and materialise),
`common/server.js` (the interval), `opencode/skill-refresh.mjs` and
`InternalController.Skills` with `LibraryAccessService.SkillPayloadsVersionAsync`.

## What each pinned CLI reads, and when

Verified against the real CLI at the version pinned in its Dockerfile. Each check uses a mock
model endpoint that records every request: the CLI runs one turn, a skill directory is added on
disk, the next turn runs, and the check looks for the new skill's description in what the model
was sent.

| Agent | Reads | Re-reads during a session | How this is used |
|---|---|---|---|
| Claude Code 2.1.287 | `~/.claude/skills` | yes, the next turn sees it | directly |
| Codex 0.160.0 | `$CODEX_HOME/skills`, `~/.agents/skills` (**not** `~/.claude/skills`) | yes, through a file watcher, also when `~/.agents/skills` is a symlink | `~/.agents/skills → ../.claude/skills` |
| OpenCode 1.18.35 | `~/.claude/skills`, `~/.agents/skills` | **no**: cached per instance (`Skill.state`) until the instance is disposed | the policy plugin disposes it when idle |
| OpenClaw 2026.7.1-2 | `~/.agents/skills`, `~/.openclaw/skills` (not `~/.claude/skills`) | **no** in `tui --local` / `agent --local` | symlink; takes effect at the next start |
| Cursor (unpinned) | `~/.cursor/skills`, `~/.agents/skills`; `~/.claude/skills` only with third-party extensibility on | not verified | symlink |

Notes on the rows that are not simply "yes":

- **OpenCode** keeps the skill list in an InstanceState, so a skill added mid-session is missing
  from every later request. After `POST /instance/dispose` it appears, and the conversation goes
  on: the TUI rebuilds the instance on its next request. This was checked in the real TUI (in a
  PTY that answers its terminal queries) and with the plugin doing the dispose. Three turns ran
  across two pulls, one adding a skill and one removing another. Each change showed up in the
  next turn, and the first turn's text was still in the last request.
- **OpenClaw** has a skills watcher, and it works: probed on its own, it bumps the snapshot
  version for a new `SKILL.md` within 250 ms. But the command path the local TUI and
  `agent --local` use (`agentCommand`) resolves the snapshot with `watch: false`. The in-process
  version only moves on watcher bumps, so the session keeps its first snapshot. A restart
  resumes the session with the new skill: the process's initial version is its start time, which
  is newer than the stored snapshot. That was verified by restarting the TUI on the same home.
- **Cursor** cannot be run here without an account, because its model calls go to its own
  service. The table comes from its bundle: the skill roots are listed in `agent-skills-catalog`
  with `.agents/skills` marked first-party, and `.claude/skills` is gated behind third-party
  extensibility. Whether it rescans during a session is open; the safe assumption is the next
  start.

## Design, and what each choice was weighed against

**1. One directory, `~/.claude/skills`, and a link `~/.agents/skills → ../.claude/skills`** for
Codex, Cursor and OpenClaw (`skills.js --link-agents`, from `sync_skill_library --link-agents` in
their entrypoints).

- *Rejected: a second materialised copy per agent.* That means two manifests to keep consistent,
  and two places the upward sync has to scan for skills the agent created. A skill created
  through `~/.agents/skills/<name>` would also be invisible to `skills-sync-up.js`. With the link
  both paths are the same directory, and Codex's watcher and OpenClaw's loader were both
  verified to follow it.
- *Rejected: the link for Claude Code and OpenCode too.* OpenCode reads both paths and would load
  every skill twice ("duplicate skill name"). Claude Code does not need it.
- An existing real `~/.agents/skills` directory is left alone and logged. Replacing it would delete
  someone's skills; the cost is that this agent then sees those instead of the library.

**2. The session agent polls; the hub does not push.** Every 60 s (`AGENTHUB_SKILLS_SYNC_SECONDS`,
`0` turns it off) `server.js` calls `pullSkills` with the ETag of its previous pull.

- *Rejected: a push from the hub on change.* Nothing reaches into a pod today. A push needs a
  channel per pod, and it still needs a poll as the fallback for a pod that missed the event. The
  pod already calls the hub on its own schedule.
- The hub computes the ETag from the skill records alone: name, id, version and `UpdatedAt` of
  each skill that wins its name. No `SKILL.md` and no file is read for it. Sharing and
  unsharing, a licence change and a name-conflict winner changing all change the set, so they
  change the tag too. An unchanged library is one `304` per session per minute.
- The tag is kept in the session agent's memory, not in the manifest. It only describes the
  directories the process that received it wrote, so a restarted process starts with one full
  download. That download also repairs a directory changed while no pull ran, and no value read
  from the agent-writable skills directory goes into a request to the hub.
- A pull that finds a change rewrites only the skills whose content hash differs. An unchanged
  directory gets no file event, so the CLIs that watch it (Claude Code, Codex) do not rescan for
  nothing.
- Writes are staged in `~/.claude/.agenthub-skills-staging` and swapped in by rename. A CLI
  rescanning mid-pull sees the old skill or the new one, never a half-written copy and never a
  second skill of the same name. Staging inside the skills directory would have created exactly
  that duplicate, since every CLI scans `*/SKILL.md` there.
- Two pulls never overlap (`.agenthub-skills-sync.lock`, taken over after two minutes). The
  entrypoint's pull and the server's first tick can coincide, and both would rename the same
  directories.

**3. A managed skill edited in the session is kept, not overwritten.** The manifest records the
content hash of what was written. On the next pull, a directory that no longer matches its hash
was edited locally. It is reported as `kept` and left in place, whether the library changed the
skill or removed it.

- *Rejected: overwrite, as the start-time sync did.* That silently undoes an improvement the agent
  made a minute ago. Before, the next restart overwrote such an edit; now it would be the next
  minute.
- *Rejected: turn the edited directory into an unmanaged one*, so the upward sync imports it.
  That would quietly fork a shared skill into the user's own library, and bring back a skill
  someone deliberately deleted. Nothing leaves the pod without the agent deciding to: the MCP
  instructions tell it to `upload_skill` an edit worth keeping. Once the library serves exactly
  the local content, the hashes agree again and the skill follows the library from then on.

**4. OpenCode: the policy plugin disposes the instance once a pull changed the skills and no
session is busy.** The plugin watches the manifest's `generation`, which `skills.js` bumps only
when a directory changed. It tracks `session.status` busy/idle and calls
`client.instance.dispose()` when nothing is running, so a pull that lands mid-turn is applied
when the turn ends. Interactive sessions only: `opencode run` is one turn, and its end is the
process's end.

- *Rejected: start the TUI with a fixed `--port` and let the session agent call the endpoint.*
  That opens an unauthenticated server for the whole pod, and it still cannot see whether a turn
  is running, which only an in-process plugin can.
- One refresher per process (module-level), replaced by each new instance's plugin call. A dispose
  that something else triggers would otherwise leave the old timer running with a stale view of
  which sessions are busy.

**5. OpenClaw stays at "next start".** *Rejected: switching the runtime from `--local` to a
long-running gateway* to get the watcher path. That is a different process model for one feature.
*Rejected: an OpenClaw plugin that calls the internal refresh functions.* Those live in
hash-named bundle chunks that change with every release, so it would break silently on the next
bump. The agent can still read any file under `~/.agents/skills` and search the library mid-session.

## What the agent is told

The skill-library MCP instructions (`SkillLibraryMcpService`) now open with the fact that the
library is already on disk and kept in sync. They say `search_skills` is for what the listing
does not show, and that an edited library skill is kept locally until uploaded. The tools stay
for search by meaning, version history and upload.

## Not done: a prompt-submit hint

The issue's optional third point was a hook that adds the two or three best-matching skills to
each user message. It is deferred. The listing is now verified live in three of the five runtimes
(Claude Code, Codex, OpenCode), and every listing already carries each skill's description. A per-prompt hint costs context on every
turn. Whether it is worth that should be decided once it is clear whether agents use listed
skills now that the listing is current.

## Re-verifying after a CLI bump

A mock model (Messages, Responses and Chat Completions, recording each request), the CLI in a PTY
via `script`, two turns with a skill added in between, then a check of both requests for the
skill's description. OpenCode's TUI needs its terminal queries answered (cursor position, device
attributes, colour queries) or it never draws. OpenClaw runs `tui --local` with a mock provider
under `models.providers`. The rig is in the skill library as `agent-skill-live-reload-rig`.
