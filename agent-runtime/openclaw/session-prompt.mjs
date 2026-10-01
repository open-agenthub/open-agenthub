import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { writeSystemPromptFile } from '../common/system-prompt-file.mjs';

/**
 * Gives an OpenClaw session the caller's extra system prompt.
 *
 * `APPEND_SYSTEM.md`, not `SYSTEM.md`: the CLI discovers both, and SYSTEM.md *replaces* the whole
 * system prompt while APPEND_SYSTEM.md is concatenated onto the end of it. Verified in the pinned
 * CLI (2026.7.1-2), which builds the prompt as `base + "\n\n" + appendSystemPrompt`. Using
 * SYSTEM.md would strip OpenClaw's own tool and gateway instructions, so one line of persona from
 * a caller would leave an agent that cannot use its tools.
 *
 * Written to the agent directory — the CLI's *global* location — rather than to the workspace's
 * `.openclaw/APPEND_SYSTEM.md`, which takes precedence over it. With a single repository the
 * working directory is the clone, so a file there would be a stray in a tree the agent may commit;
 * and leaving project precedence free means a repository that ships its own file still wins.
 *
 * OpenClaw has no folder-trust dialog, so unlike Claude, Codex and Cursor there is nothing else to
 * pre-accept here.
 */
export function writeOpenClawSystemPrompt(agentDir, systemPrompt) {
  return writeSystemPromptFile(
    path.join(agentDir, 'APPEND_SYSTEM.md'), systemPrompt,
    { header: '# Session instructions\n\nStanding instructions for this AgentHub session.' });
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href)
  writeOpenClawSystemPrompt(process.argv[2], process.env.AGENTHUB_SYSTEM_PROMPT);
