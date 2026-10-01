// The skill-library MCP server as the agent sees it: a stdio proxy in front of the hub's
// HTTP endpoint.
//
// Why a proxy rather than more tools on the hub: `upload_skill(path)` and
// `get_skill(out_dir)` are filesystem operations, and the hub is not on this filesystem.
// Without them a helper script has to be quoted into a tool call to get into the library
// and quoted back out to leave it, paying for every byte twice in context — which in
// practice meant larger scripts were never uploaded at all. Here the agent names a path.
//
// Why a byte-level proxy rather than a reimplementation: everything except those two
// arguments is forwarded untouched, including tools the hub grows later. The hub stays the
// single definition of the tools, their descriptions and their scoping rules; this file
// only adds the two arguments and the file handling behind them.

import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { SkillsBackendClient } from './client.mjs';
import { collectSource, writeBundle } from './collect.mjs';

const PATH_ARGUMENT_DOC =
  'Local path to upload instead of inlining the files: a skill directory (its SKILL.md and '
  + 'the files next to it), a single helper script, or a .tar.gz. The content is read here in '
  + 'the session and sent straight to the hub, so it never passes through your context — a '
  + 'large script is no reason to skip the upload. With a directory or archive its SKILL.md is '
  + 'used unless you also pass content.';

const OUT_DIR_ARGUMENT_DOC =
  'Directory to write the skill into (SKILL.md plus every extra file). The files are written '
  + 'here in the session, so their content never passes through your context — the result only '
  + 'names the paths. Use this instead of reading files one by one.';

/** Adds the two filesystem arguments to the schemas the hub advertises. */
export function augmentTools(toolsResult) {
  const tools = toolsResult?.tools;
  if (!Array.isArray(tools)) return toolsResult;
  for (const tool of tools) {
    const properties = tool?.inputSchema?.properties;
    if (!properties) continue;
    if (tool.name === 'upload_skill') {
      properties.path = { type: 'string', description: PATH_ARGUMENT_DOC };
      tool.description = `${tool.description} Prefer 'path' over 'files' whenever the files `
        + 'are already on disk.';
    }
    if (tool.name === 'get_skill') {
      properties.out_dir = { type: 'string', description: OUT_DIR_ARGUMENT_DOC };
    }
  }
  return toolsResult;
}

/** Appends the proxy's note to the instructions the hub returns on initialize. */
export function augmentInstructions(initializeResult) {
  if (typeof initializeResult?.instructions !== 'string') return initializeResult;
  initializeResult.instructions += '\nThis session reaches the library through its runtime, so '
    + 'files never need to pass through your context: upload_skill takes a local path (directory, '
    + 'script or .tar.gz) and get_skill takes out_dir to write the skill to disk.';
  return initializeResult;
}

function textResult(text) {
  return { content: [{ type: 'text', text }] };
}

function errorResult(text) {
  return { content: [{ type: 'text', text }], isError: true };
}

function describeSkipped(skipped) {
  if (!skipped.length) return '';
  return `\nLeft out (${skipped.length}): ${skipped.join('; ')}`;
}

function formatSize(chars) {
  return chars >= 1024 ? `${(chars / 1024).toFixed(1)} KB` : `${chars} characters`;
}

export function createProxy(options = {}) {
  const client = options.client ?? new SkillsBackendClient();
  const deps = { fs: options.fs, run: options.run, tmpdir: options.tmpdir };

  /** upload_skill(path): read the files here, hand the hub an ordinary inline upload. */
  async function uploadFromPath(request) {
    const args = { ...request.params.arguments };
    const source = args.path;
    delete args.path;

    const collected = await collectSource(source, deps);
    if (!args.content && !collected.content) {
      return errorResult(
        `No SKILL.md found at ${collected.source} and no content argument given. Pass content, or `
        + 'put a SKILL.md next to the files.');
    }
    if (!args.content) args.content = collected.content;
    // An explicit empty files array is how the agent clears a skill's files; a path upload
    // always replaces the set with what is on disk, which may legitimately be nothing.
    args.files = collected.files;

    const response = await client.rpc({ ...request, params: { ...request.params, arguments: args } });
    const result = response?.result;
    if (!result || result.isError) return response;

    const read = collected.files.length === 1
      ? `1 file (${formatSize(collected.chars)})`
      : `${collected.files.length} files (${formatSize(collected.chars)})`;
    const existing = result.content?.[0]?.text ?? '';
    return {
      ...response,
      result: {
        ...result,
        content: [{
          type: 'text',
          text: `${existing}\nRead from ${collected.source} (${collected.kind}): ${read} — none of it `
            + `passed through your context.${describeSkipped(collected.skipped)}`
        }]
      }
    };
  }

  /** get_skill(out_dir): forward the read, then put the files on disk from the bundle route. */
  async function getIntoDirectory(request) {
    const args = { ...request.params.arguments };
    const outDir = args.out_dir;
    delete args.out_dir;
    if (args.file) {
      return errorResult("Pass either 'file' (read one file as text) or 'out_dir' (write them all "
        + 'to disk), not both.');
    }

    const response = await client.rpc({ ...request, params: { ...request.params, arguments: args } });
    const result = response?.result;
    if (!result || result.isError) return response;

    const archive = await client.bundle(args.name, args.version);
    const { dir, written } = await writeBundle(outDir, archive, deps);
    // Reported the way the library stores them, so the names in the reply match the names
    // the skill's own files have.
    const relative = written.map(file => path.relative(dir, file).split(path.sep).join('/')).sort();
    const skillMd = written.find(file => path.basename(file) === 'SKILL.md');
    const extras = relative.filter(file => file !== 'SKILL.md');

    const existing = result.content?.[0]?.text ?? '';
    return {
      ...response,
      result: {
        ...result,
        content: [{
          type: 'text',
          text: `${existing}\n\nWritten to ${dir}: `
            + `${extras.length === 0 ? 'SKILL.md only' : `SKILL.md and ${extras.length} file(s) — ${extras.join(', ')}`}`
            + `${skillMd ? '' : ' (no SKILL.md in the bundle)'}`
        }]
      }
    };
  }

  /** Handles one JSON-RPC message; null means the message needs no reply. */
  async function handle(message) {
    const isToolCall = message?.method === 'tools/call' && message.params?.arguments;
    if (isToolCall) {
      const name = message.params.name;
      const args = message.params.arguments;
      try {
        if (name === 'upload_skill' && typeof args.path === 'string' && args.path.length > 0)
          return reply(message, await uploadFromPath(message));
        if (name === 'get_skill' && typeof args.out_dir === 'string' && args.out_dir.length > 0)
          return reply(message, await getIntoDirectory(message));
      } catch (error) {
        return reply(message, errorResult(describeError(error)));
      }
    }

    const response = await client.rpc(message);
    if (!response) return null;
    if (message?.method === 'tools/list' && response.result) augmentTools(response.result);
    if (message?.method === 'initialize' && response.result) augmentInstructions(response.result);
    return response;
  }

  /** Wraps a tool result (or a full response) into the reply for this request. */
  function reply(message, resultOrResponse) {
    if (resultOrResponse && typeof resultOrResponse === 'object' && 'jsonrpc' in resultOrResponse)
      return resultOrResponse;
    return { jsonrpc: '2.0', id: message.id ?? null, result: resultOrResponse };
  }

  return { handle };
}

function describeError(error) {
  const message = error instanceof Error ? error.message : String(error);
  // The path errors name the path the agent passed, which is exactly what it needs to fix
  // the call; the backend codes are opaque on purpose and stay as they are.
  return message.startsWith('skills_')
    ? message.replace(/^skills_/, '').replace(/_/g, ' ')
    : `skill operation failed: ${message}`;
}

/**
 * Runs the proxy over stdio. MCP's stdio transport frames messages as one JSON object per
 * line, so a partial line is held until its newline arrives rather than parsed and dropped.
 */
export async function run(proxy, input = process.stdin, output = process.stdout) {
  input.setEncoding('utf8');
  let buffer = '';
  let queue = Promise.resolve();

  for await (const chunk of input) {
    buffer += chunk;
    let newline;
    while ((newline = buffer.indexOf('\n')) >= 0) {
      const line = buffer.slice(0, newline).trim();
      buffer = buffer.slice(newline + 1);
      if (!line) continue;
      // Requests are answered in the order they arrived: a client is free to send the next
      // one before the previous reply, and reordered responses to a batch of reads read as
      // a protocol error rather than as slow file writes.
      queue = queue.then(async () => {
        let message;
        try {
          message = JSON.parse(line);
        } catch {
          output.write(`${JSON.stringify({
            jsonrpc: '2.0', id: null, error: { code: -32700, message: 'Parse error' }
          })}\n`);
          return;
        }
        try {
          const response = await proxy.handle(message);
          if (response) output.write(`${JSON.stringify(response)}\n`);
        } catch (error) {
          if (message.id === undefined || message.id === null) return; // notification
          output.write(`${JSON.stringify({
            jsonrpc: '2.0',
            id: message.id,
            error: { code: -32603, message: describeError(error) }
          })}\n`);
        }
      });
    }
  }
  await queue;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  await run(createProxy());
}
