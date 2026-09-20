import fs from 'node:fs';
import { pathToFileURL } from 'node:url';

export const networkMcp = Object.freeze({
  command: 'node',
  args: ['/opt/session-agent/network/server.mjs']
});

export function mergeNetworkMcp(userConfig = {}, runtime = '/opt/session-agent') {
  const servers = userConfig?.mcpServers;
  if (servers !== undefined && (servers === null || typeof servers !== 'object' || Array.isArray(servers)))
    throw new Error('invalid_mcp_config');
  return {
    ...userConfig,
    mcpServers: {
      ...(servers ?? {}),
      agenthub_network: { command: 'node', args: [`${runtime}/network/server.mjs`] }
    }
  };
}

export function writeNetworkMcp(inputPath, outputPath = '/tmp/agenthub-mcp.json') {
  let userConfig = {};
  if (inputPath && fs.existsSync(inputPath)) {
    const text = fs.readFileSync(inputPath, 'utf8');
    userConfig = JSON.parse(text);
  }
  const runtime = process.env.RUNTIME || '/opt/session-agent';
  fs.writeFileSync(outputPath, `${JSON.stringify(mergeNetworkMcp(userConfig, runtime))}\n`, { mode: 0o600 });
  return outputPath;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href)
  writeNetworkMcp(process.argv[2], process.argv[3]);
