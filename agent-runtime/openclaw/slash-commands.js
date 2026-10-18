'use strict';

// Read off the "/" menu and /help of the OpenClaw 2026.7.1-2 TUI (the pinned version) in a PTY;
// re-record it when OPENCLAW_VERSION changes (docs/agent-runtime-updates.md). Aliases the
// menu lists separately (/elev, /gwstatus, /quit, /t, /v) are left out; they run the same.
const NEW_CONVERSATION = 'new-conversation';
const builtins = [
  ['abort', 'Abort active run'],
  ['activation', 'Set group activation', '<mention|always>'],
  ['agent', 'Switch agent (or open picker)', '[id]'],
  ['agents', 'Open agent picker'],
  ['auth', 'Run provider auth/login flow', '[provider]'],
  ['btw', 'Ask a side question without changing future session context'],
  ['elevated', 'Set elevated on/off/ask/full', '<on|off|ask|full>'],
  ['exit', 'Exit the TUI'],
  ['fast', 'Set fast mode auto/on/off', '<status|auto|on|off>'],
  ['gateway-status', 'Show gateway status summary'],
  ['goal', 'Show or control the current goal'],
  ['help', 'Show slash command help'],
  ['model', 'Set model (or open picker)', '[provider/model]'],
  ['models', 'Open model picker'],
  ['new', 'Spawn a new isolated session', null, NEW_CONVERSATION],
  ['reasoning', 'Set reasoning on/off', '<on|off>'],
  ['reset', 'Reset the current session', null, NEW_CONVERSATION],
  ['session', 'Switch session (or open picker)', '[key]', NEW_CONVERSATION],
  ['sessions', 'Open session picker', null, NEW_CONVERSATION],
  ['settings', 'Open settings'],
  ['stop', 'Stop the current run'],
  ['think', 'Set thinking level', '<off|minimal|low|medium|high|xhigh>'],
  ['trace', 'Set trace on/off', '<on|off>'],
  ['usage', 'Toggle per-response usage line', '<off|tokens|full|reset|inherit|clear|default>'],
  ['verbose', 'Set verbose on/off', '<on|off>']
].map(([name, description, argumentHint, effect]) => ({ name, description, argumentHint, effect }));

module.exports = { cli: 'OpenClaw 2026.7.1-2', builtins };
