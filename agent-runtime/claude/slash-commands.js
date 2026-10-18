'use strict';

const path = require('node:path');

// Read off the "/" menu of Claude Code 2.1.287 (the pinned version) in a PTY with an empty HOME;
// re-record it when CLAUDE_CODE_VERSION changes (docs/agent-runtime-updates.md). Skills the CLI
// bundles behind feature flags are left out: whether one exists depends on the account, and a
// person can still type it — the composer sends any text.
const NEW_CONVERSATION = 'new-conversation';
const builtins = [
  ['add-dir', 'Add a new working directory'],
  ['advisor', 'Let Claude consult a stronger model at key moments'],
  ['autocompact', 'Set how full the context gets before auto-summarizing'],
  ['branch', 'Create a branch of the current conversation at this point', null, NEW_CONVERSATION],
  ['btw', 'Ask a quick side question without interrupting the main conversation'],
  ['bug', 'Report a bug or share your conversation'],
  ['clear', 'Start a new session with empty context', null, NEW_CONVERSATION],
  ['color', 'Set the prompt bar color for this session'],
  ['compact', 'Free up context by summarizing the conversation so far'],
  ['config', 'Open settings'],
  ['context', 'Visualize current context usage as a colored grid'],
  ['copy', "Copy Claude's last response to clipboard"],
  ['diff', 'Toggle the diff panel showing uncommitted changes'],
  ['doctor', 'Health-check the Claude Code setup'],
  ['effort', 'Set effort level for model usage'],
  ['exit', 'Exit the CLI'],
  ['export', 'Export the current conversation to a file or clipboard'],
  ['fast', 'Toggle fast mode'],
  ['feedback', 'Send feedback or report a bug'],
  ['focus', 'Toggle focus view: just your prompt, summary, and response'],
  ['goal', 'Set a goal Claude checks before stopping'],
  ['help', 'Show help and available commands'],
  ['hooks', 'View hook configurations for tool events'],
  ['init', 'Initialize a new CLAUDE.md file with codebase documentation'],
  ['insights', 'Generate a report analyzing your Claude Code sessions'],
  ['keybindings', 'Open your keyboard shortcuts file'],
  ['list-agents', 'List subagents, teammates, and other Claude sessions you can message'],
  ['login', 'Switch accounts'],
  ['logout', 'Sign out'],
  ['mcp', 'Manage MCP servers'],
  ['memory', 'Edit CLAUDE.md files and memory settings'],
  ['model', 'Set the AI model for Claude Code'],
  ['output-style', 'List output styles or switch to one'],
  ['permissions', 'Manage allow and deny tool permission rules'],
  ['plan', 'Enable plan mode or view the current session plan'],
  ['plugin', 'Manage Claude Code plugins'],
  ['recap', 'Generate a one-line session recap now'],
  ['release-notes', 'View release notes'],
  ['reload-plugins', 'Activate pending plugin changes in the current session'],
  ['reload-skills', 'Pick up skills added or changed on disk during this session'],
  ['rename', 'Rename the current conversation'],
  ['resume', 'Resume a previous conversation', null, NEW_CONVERSATION],
  ['rewind', 'Restore the code and/or conversation to a previous point'],
  ['security-review', 'Complete a security review of the pending changes on the current branch'],
  ['skills', 'List available skills'],
  ['status', 'Show Claude Code status including version, model, account and API'],
  ['statusline', "Set up Claude Code's status line UI"],
  ['subtask', 'Send a subagent off with your full context'],
  ['tasks', 'View and manage everything running in the background'],
  ['theme', 'Change the theme'],
  ['usage', 'Show session cost, plan usage, and activity stats']
].map(([name, description, argumentHint, effect]) => ({ name, description, argumentHint, effect }));

// Where Claude Code loads custom commands and skills from: the user's and the project's
// .claude directory. Skills are invoked as /<name> like commands.
function sources({ home, cwd }) {
  return [
    { kind: 'commands', dir: path.join(cwd, '.claude', 'commands'), namespaced: true },
    { kind: 'commands', dir: path.join(home, '.claude', 'commands'), namespaced: true },
    { kind: 'skills', dir: path.join(cwd, '.claude', 'skills') },
    { kind: 'skills', dir: path.join(home, '.claude', 'skills') }
  ];
}

module.exports = { cli: 'Claude Code 2.1.287', builtins, sources };
