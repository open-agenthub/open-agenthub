'use strict';

const path = require('node:path');

// Read off the "/" menu of OpenCode 1.18.35 (the pinned version) in a PTY; re-record it when
// OPENCODE_VERSION changes (docs/agent-runtime-updates.md). Skills are not part of that menu
// (they have their own /skills dialog), custom commands are.
const NEW_CONVERSATION = 'new-conversation';
const builtins = [
  ['agents', 'Switch agent'],
  ['connect', 'Connect provider'],
  ['debug', 'View debug info'],
  ['diff', 'Open diff viewer'],
  ['editor', 'Open editor'],
  ['exit', 'Exit the app'],
  ['help', 'Help'],
  ['init', 'Guided AGENTS.md setup'],
  ['mcps', 'Toggle MCPs'],
  ['models', 'Switch model'],
  ['move', 'Move to another project dir'],
  ['new', 'New session', null, NEW_CONVERSATION],
  ['review', 'Review changes, defaults to uncommitted', '[commit|branch|pr]'],
  ['sessions', 'Switch session', null, NEW_CONVERSATION],
  ['skills', 'Skills'],
  ['status', 'View status'],
  ['themes', 'Switch theme']
].map(([name, description, argumentHint, effect]) => ({ name, description, argumentHint, effect }));

// The user config the entrypoint writes (AGENTHUB_OPENCODE_CONFIG), the project's own config,
// and the command directories OpenCode reads in both places.
function sources({ env, home, cwd }) {
  const userDir = path.join(home, '.config', 'opencode');
  return [
    { kind: 'config', file: env.AGENTHUB_OPENCODE_CONFIG || path.join(userDir, 'opencode.json') },
    { kind: 'config', file: path.join(cwd, 'opencode.json') },
    { kind: 'commands', dir: path.join(cwd, '.opencode', 'command') },
    { kind: 'commands', dir: path.join(cwd, '.opencode', 'commands') },
    { kind: 'commands', dir: path.join(userDir, 'command') },
    { kind: 'commands', dir: path.join(userDir, 'commands') }
  ];
}

module.exports = { cli: 'OpenCode 1.18.35', builtins, sources };
