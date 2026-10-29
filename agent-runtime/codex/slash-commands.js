'use strict';

// Read off the "/" menu of Codex 0.165.0 (the pinned version) in a PTY; re-record it when
// CODEX_VERSION changes (docs/agent-runtime-updates.md). This version lists no custom prompt
// files under "/" and invokes skills with "$name", whose popup takes the Enter the composer
// sends as "insert" rather than "submit" — so neither is offered here.
const NEW_CONVERSATION = 'new-conversation';
const builtins = [
  ['agents', 'Open the agent command center'],
  ['approve', 'Approve one retry of a recent auto-review denial'],
  ['cd', 'Change the current working directory'],
  ['clear', 'Clear the terminal and start a new chat', null, NEW_CONVERSATION],
  ['compact', 'Summarize conversation to prevent hitting the context limit'],
  ['config', 'Configure Codex preferences'],
  ['copy', 'Copy the last response or part of it'],
  ['daybreak', 'Manage broader access for cybersecurity work'],
  ['diff', 'Show git diff (including untracked files)'],
  ['exit', 'Exit Codex'],
  ['experimental', 'Toggle experimental features'],
  ['export', 'Export the conversation as markdown'],
  ['fast', '2x speed, increased usage'],
  ['feedback', 'Send logs to maintainers'],
  ['fork', 'Fork the current chat', null, NEW_CONVERSATION],
  ['goal', 'Set or view the goal for a long-running task'],
  ['hooks', 'View and manage lifecycle hooks'],
  ['init', 'Create an AGENTS.md file with instructions for Codex'],
  ['logout', 'Log out of Codex'],
  ['mcp', 'List MCP tools; use /mcp verbose or /mcp login <name>', '[verbose|login <name>]'],
  ['memories', 'Configure memory use and generation'],
  ['mention', 'Mention a file'],
  ['model', 'Choose what model and reasoning effort to use'],
  ['new', 'Start a new chat during a conversation', null, NEW_CONVERSATION],
  ['permissions', 'Choose what Codex is allowed to do'],
  ['plan', 'Switch to Plan mode'],
  ['plugins', 'Browse plugins'],
  ['presets', 'Run or save reusable prompts and slash commands'],
  ['ps', 'List background terminals'],
  ['pwd', 'Show the current working directory'],
  ['recap', 'Summarize the current conversation now'],
  ['rename', 'Rename the current thread'],
  ['resume', 'Resume a saved chat', null, NEW_CONVERSATION],
  ['review', 'Review my current changes and find issues'],
  ['side', 'Start a side conversation in an ephemeral fork'],
  ['skills', 'Use skills to improve how Codex performs specific tasks'],
  ['status', 'Show current session configuration and token usage'],
  ['statusline', 'Configure which items appear in the status line'],
  ['stop', 'Stop all background terminals'],
  ['subagents', "Switch between this session's subagents"],
  ['theme', 'Choose a syntax highlighting theme'],
  ['warnings', 'View retained warnings and diagnostic details']
].map(([name, description, argumentHint, effect]) => ({ name, description, argumentHint, effect }));

module.exports = { cli: 'Codex 0.165.0', builtins };
