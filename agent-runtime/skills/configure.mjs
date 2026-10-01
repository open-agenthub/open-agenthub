import fs from 'node:fs';
import { pathToFileURL } from 'node:url';

export const SERVER_NAME = 'skill-library';

/**
 * Swaps the hub's HTTP skill-library entry for the local stdio proxy.
 *
 * Replaces rather than adds, and only when the entry is already there: the hub injects it
 * exactly when the feature is enabled for the session (SkillLibraryMcp), so keying off the
 * entry means the proxy follows that switch without a second one to keep in sync. The
 * proxy reads the same callback url and token from the environment, so nothing of the
 * entry is needed beyond its presence.
 *
 * An entry the user configured themselves (a stdio command of their own, a url elsewhere)
 * is left alone — only the hub's own http entry is taken over.
 */
export function mergeSkillsMcp(userConfig = {}, runtime = '/opt/session-agent', env = process.env) {
  const servers = userConfig?.mcpServers;
  if (servers !== undefined && (servers === null || typeof servers !== 'object' || Array.isArray(servers)))
    throw new Error('invalid_mcp_config');
  const existing = servers?.[SERVER_NAME];
  if (env.AGENTHUB_SKILLS_MCP_ENABLED === '0' || !isHubEntry(existing)) return userConfig;

  return {
    ...userConfig,
    mcpServers: {
      ...servers,
      [SERVER_NAME]: { command: 'node', args: [`${runtime}/skills/server.mjs`] }
    }
  };
}

/** True for the entry SkillLibraryMcpConfig.BuildServer emits: http with a url. */
export function isHubEntry(entry) {
  if (!entry || typeof entry !== 'object' || Array.isArray(entry)) return false;
  if (typeof entry.url !== 'string' || entry.command !== undefined) return false;
  return entry.type === undefined || entry.type === 'http' || entry.type === 'streamable-http';
}

export function writeSkillsMcp(inputPath, outputPath = '/tmp/agenthub-mcp.json') {
  let userConfig = {};
  if (inputPath && fs.existsSync(inputPath)) userConfig = JSON.parse(fs.readFileSync(inputPath, 'utf8'));
  const merged = mergeSkillsMcp(userConfig, process.env.RUNTIME || '/opt/session-agent');
  fs.writeFileSync(outputPath, `${JSON.stringify(merged)}\n`, { mode: 0o600 });
  return outputPath;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href)
  writeSkillsMcp(process.argv[2], process.argv[3]);
