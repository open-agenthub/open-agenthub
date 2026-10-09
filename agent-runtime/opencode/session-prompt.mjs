import { pathToFileURL } from 'node:url';
import { writeSystemPromptFile } from '../common/system-prompt-file.mjs';

/**
 * Gives an OpenCode session the caller's extra system prompt.
 *
 * OpenCode has no system-prompt flag, but its config takes `instructions`: files whose content is
 * added to the system prompt next to AGENTS.md. user-config.js lists this file there whenever it
 * exists. It lives in the config directory under $HOME rather than in the workspace, because with a
 * single repository the working directory is the clone, and a file there is a stray in a tree the
 * agent may commit. A repository's own AGENTS.md keeps being read either way.
 */
export function writeOpenCodeSystemPrompt(filePath, systemPrompt) {
  return writeSystemPromptFile(filePath, systemPrompt,
    { header: '# Session instructions\n\nStanding instructions for this AgentHub session.' });
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href)
  writeOpenCodeSystemPrompt(process.argv[2], process.env.AGENTHUB_SYSTEM_PROMPT);
