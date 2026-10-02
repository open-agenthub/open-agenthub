import fs from 'node:fs';
import path from 'node:path';

/**
 * Writes the caller's extra system prompt to the file an agent CLI reads it from — or removes
 * that file when there is no prompt.
 *
 * The removal is the part that matters. Codex and OpenClaw read these files from inside their
 * state directory, and the state directory is restored from the session's own tar on every start.
 * Leaving the file alone when `AGENTHUB_SYSTEM_PROMPT` is empty would let a prompt from an earlier
 * incarnation of the session keep applying, which looks like the agent inventing rules nobody
 * gave it. Writing or deleting on every start makes the file say exactly what the request said.
 *
 * Only the three runtimes that need a file use this. Claude takes the same text as
 * `--append-system-prompt` on the command line and needs no file at all.
 */
export function writeSystemPromptFile(filePath, systemPrompt, { header } = {}) {
  const text = typeof systemPrompt === 'string' ? systemPrompt.trim() : '';
  if (!text) {
    fs.rmSync(filePath, { force: true });
    return null;
  }
  fs.mkdirSync(path.dirname(filePath), { recursive: true });
  fs.writeFileSync(filePath, `${header ? `${header}\n\n` : ''}${text}\n`, { mode: 0o600 });
  return filePath;
}
