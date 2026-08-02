import fs from 'node:fs';
import { pathToFileURL } from 'node:url';

export function mergeFilesMcp(userConfig = {}, runtime = '/opt/session-agent') {
  const servers = userConfig?.mcpServers;
  if (servers !== undefined && (servers === null || typeof servers !== 'object' || Array.isArray(servers)))
    throw new Error('invalid_mcp_config');
  return {
    ...userConfig,
    mcpServers: {
      ...(servers ?? {}),
      agenthub_files: { command: 'node', args: [`${runtime}/files/server.mjs`] }
    }
  };
}

export function writeFilesMcp(inputPath, outputPath = '/tmp/agenthub-mcp.json') {
  let userConfig = {};
  if (inputPath && fs.existsSync(inputPath)) userConfig = JSON.parse(fs.readFileSync(inputPath, 'utf8'));
  fs.writeFileSync(outputPath, `${JSON.stringify(mergeFilesMcp(userConfig, process.env.RUNTIME || '/opt/session-agent'))}\n`, { mode: 0o600 });
  return outputPath;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href)
  writeFilesMcp(process.argv[2], process.argv[3]);
