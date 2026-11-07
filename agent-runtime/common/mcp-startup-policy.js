'use strict';

const flags = {
  agenthub_browser: 'AGENTHUB_BROWSER_ENABLED',
  agenthub_sessions: 'AGENTHUB_SPAWN_MCP_ENABLED',
  agenthub_network: 'AGENTHUB_NETWORK_MCP_ENABLED',
  agenthub_files: 'AGENTHUB_FILES_MCP_ENABLED',
  'skill-library': 'AGENTHUB_SKILLS_MCP_ENABLED'
};
const blocked = JSON.parse(process.env.AGENTHUB_BLOCKED_MCP_SERVERS || '[]');
if (!Array.isArray(blocked) || blocked.some(name => typeof name !== 'string')) {
  throw new Error('Invalid session MCP startup policy');
}
for (const name of blocked) {
  if (Object.hasOwn(flags, name)) process.stdout.write(`export ${flags[name]}=0\n`);
}
