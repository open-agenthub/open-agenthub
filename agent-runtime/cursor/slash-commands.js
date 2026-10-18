'use strict';

const path = require('node:path');

// Cursor is deliberately unpinned (docs/agent-runtime-updates.md), and its TUI does not start
// without a real login, so this list was read from the command table in the 2026.10.16 CLI
// bundle rather than from its menu; entries hidden behind internal flags are left out. A newer
// build may add commands — typed ones are still sent as they are.
const NEW_CONVERSATION = 'new-conversation';
const builtins = [
  ['add-dir', 'Add a directory to the current workspace'],
  ['ask', 'Toggle ask mode (Q&A, read-only)'],
  ['btw', 'Ask on the side without disrupting the main chat'],
  ['changes', 'Review changes'],
  ['clear', 'Start a new chat session', null, NEW_CONVERSATION],
  ['command', 'Manage custom commands'],
  ['commit', 'Ask the agent to stage and commit changes'],
  ['config', 'Configure CLI settings interactively'],
  ['context', 'Show context usage breakdown'],
  ['copy', 'Copy a previous message to the clipboard'],
  ['debug', 'Toggle debug mode or submit a prompt in debug mode', '[prompt]'],
  ['exit', 'Exit'],
  ['fast', 'Toggle fast mode'],
  ['feedback', 'Share feedback with the team'],
  ['fork', 'Fork the current chat into a new session', null, NEW_CONVERSATION],
  ['goal', 'Start a durable goal that continues while idle', '<objective>'],
  ['help', 'Show help', '[cmd]'],
  ['jobs', 'Open active tasks list'],
  ['logout', 'Sign out'],
  ['mcp', 'Manage MCP servers', '[list|list-tools]'],
  ['model', 'Select model'],
  ['plan', 'Create a plan or show existing plan with options', '[prompt]'],
  ['plugin', 'Manage plugins'],
  ['rename', 'Rename the current chat session'],
  ['resume', 'Open recent chats and resume one', null, NEW_CONVERSATION],
  ['rewind', 'Jump back to a previous message'],
  ['rule', 'Manage rules'],
  ['skills', 'Open skills menu'],
  ['summarize', 'Summarize the conversation to reduce context'],
  ['usage', 'Show plan and on-demand usage']
].map(([name, description, argumentHint, effect]) => ({ name, description, argumentHint, effect }));

// The command directories the bundle loads: the project's and the user's, in both its own and
// Claude Code's layout.
function sources({ home, cwd }) {
  return [
    { kind: 'commands', dir: path.join(cwd, '.cursor', 'commands') },
    { kind: 'commands', dir: path.join(cwd, '.claude', 'commands') },
    { kind: 'commands', dir: path.join(home, '.cursor', 'commands') },
    { kind: 'commands', dir: path.join(home, '.claude', 'commands') }
  ];
}

module.exports = { cli: 'Cursor (unpinned)', builtins, sources };
