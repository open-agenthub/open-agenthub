import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { writeSystemPromptFile } from '../common/system-prompt-file.mjs';

/**
 * Gives a Codex session the caller's extra system prompt, and pre-accepts the folder-trust
 * question that would otherwise stop it before it starts.
 *
 * Verified against the pinned CLI (0.157.1).
 */

/**
 * The caller's instructions go to `$CODEX_HOME/AGENTS.md`, the *global* project doc.
 *
 * Codex has `model.base_instructions`, but that replaces its base system prompt and takes its
 * tool and sandbox instructions with it — the same trap as Claude's `--system-prompt`. The global
 * AGENTS.md is additive instead: `codex debug prompt-input` renders both docs inside one
 * `<INSTRUCTIONS>` block, the global one first, then `--- project-doc ---`, then the repository's
 * own AGENTS.md. So a caller adding a line of persona cannot silently drop the rules the checked
 * out repository ships.
 *
 * Global rather than a file in the workspace for the same reason: with a single repository the
 * session's working directory *is* the clone, so writing AGENTS.md there would either overwrite
 * the repository's own or leave a stray file in a tree the agent is about to commit.
 */
export function writeCodexSystemPrompt(codexHome, systemPrompt) {
  return writeSystemPromptFile(
    path.join(codexHome, 'AGENTS.md'), systemPrompt,
    { header: '# Session instructions\n\nStanding instructions for this AgentHub session.' });
}

/** Escapes a path for a TOML basic string, so a quote or backslash cannot end the key early. */
function tomlBasicString(value) {
  return `"${value.replace(/\\/g, '\\\\').replace(/"/g, '\\"')}"`;
}

/**
 * Marks the session's working directory trusted in `$CODEX_HOME/config.toml`.
 *
 * Without it the TUI opens on "Trust this folder? Codex can read, edit, and run files here" and
 * runs nothing until somebody answers, so a session created with a task would have made no
 * progress by the time a person took it over. `codex exec` does not ask, which is why autonomous
 * sessions never hit this and interactive ones do.
 *
 * Appended, because the entrypoint writes config.toml from scratch on every start and the MCP
 * server tables are already in it — a rewrite here would drop them.
 *
 * Nothing is consented to that the user did not already choose: the directory holds the
 * repositories their own account asked this session to check out.
 */
export function trustCodexProject(configPath, dir) {
  fs.appendFileSync(
    configPath, `\n[projects.${tomlBasicString(dir)}]\ntrust_level = "trusted"\n`, { mode: 0o600 });
  return configPath;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const [codexHome, configPath, workdir] = process.argv.slice(2);
  writeCodexSystemPrompt(codexHome, process.env.AGENTHUB_SYSTEM_PROMPT);
  trustCodexProject(configPath, workdir);
}
