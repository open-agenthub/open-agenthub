import fs from 'node:fs';
import { pathToFileURL } from 'node:url';

export const browserMcp = Object.freeze({
  command: 'node',
  args: ['/opt/session-agent/browser/server.mjs']
});

export function mergeClaudeMcp(userConfig = {}, runtime = '/opt/session-agent') {
  const servers = userConfig?.mcpServers;
  if (servers !== undefined && (servers === null || typeof servers !== 'object' || Array.isArray(servers)))
    throw new Error('invalid_mcp_config');
  return {
    ...userConfig,
    mcpServers: { ...(servers ?? {}), agenthub_browser: { command: 'node', args: [`${runtime}/browser/server.mjs`] } }
  };
}

export function writeClaudeMcp(inputPath, outputPath = '/tmp/agenthub-mcp.json') {
  let userConfig = {};
  if (inputPath && fs.existsSync(inputPath)) {
    const text = fs.readFileSync(inputPath, 'utf8');
    userConfig = JSON.parse(text);
  }
  fs.writeFileSync(outputPath, `${JSON.stringify(mergeClaudeMcp(userConfig, process.env.RUNTIME))}\n`, { mode: 0o600 });
  return outputPath;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href)
  writeClaudeMcp(process.argv[2]);