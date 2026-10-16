'use strict';

const fs = require('node:fs');
const path = require('node:path');

/**
 * The managed OpenCode config, /etc/opencode/opencode.json.
 *
 * OpenCode merges every config it finds and this one last, so it outranks whatever the agent can
 * write under $HOME or into the workspace. That is why the policy plugin is registered here and
 * nowhere else: from the user config the agent could simply drop it, and every tool call would run
 * unasked. The pod mounts the directory read-only; an init container writes it, because the plugin
 * path depends on whether the session runs the stock image or a custom one.
 *
 * `permission: "allow"` hands the decision to the plugin entirely. Left at OpenCode's defaults,
 * a call the hub had just approved would be asked again — in `opencode run` that second question
 * is answered by rejecting it.
 */
function managedConfig(runtimeDir) {
  if (typeof runtimeDir !== 'string' || !path.isAbsolute(runtimeDir)) {
    throw new Error('runtime directory must be an absolute path');
  }
  return {
    $schema: 'https://opencode.ai/config.json',
    autoupdate: false,
    share: 'disabled',
    plugin: ['file://' + path.join(runtimeDir, 'policy-plugin.mjs')],
    permission: 'allow'
  };
}

function writeManagedConfig(outputPath, runtimeDir) {
  fs.mkdirSync(path.dirname(outputPath), { recursive: true });
  fs.writeFileSync(outputPath, JSON.stringify(managedConfig(runtimeDir), null, 2) + '\n', { mode: 0o644 });
  return outputPath;
}

if (require.main === module) {
  try {
    writeManagedConfig(process.argv[2], process.argv[3]);
    console.log('[opencode] managed config written to ' + process.argv[2]);
  } catch (error) {
    console.error('[opencode] managed config failed: ' + error.message);
    process.exit(1);
  }
}

module.exports = { managedConfig, writeManagedConfig };
