'use strict';

// Cursor's hooks.json (cursor.com/docs/hooks). The skill reminder and the chat-relay
// notification live here; the permission policy is enforced through cli-config.json, not hooks.
//
// Cursor's stop hook is the same moment as Claude's and Codex's, and its payload carries
// neither a transcript nor a tool name — so the two marker hooks below are what tell the
// reminder whether the turn actually changed anything, and the relay notification can only
// say that the turn ended, not what the agent said.

function hooksConfig(runtime = process.env.RUNTIME || '/opt/session-agent') {
  const reminder = `node ${runtime}/common/skill-reminder-hook.mjs`;
  const notify = `node ${runtime}/common/turn-notify-hook.mjs`;
  return JSON.stringify({
    version: 1,
    hooks: {
      afterFileEdit: [{ command: `${reminder} --mark work`, timeout: 10 }],
      beforeShellExecution: [{ command: `${reminder} --mark work`, timeout: 10 }],
      stop: [
        // loop_limit on top of the hook's own loop_count check: one reminder per turn is the
        // whole intent, and Cursor auto-submits the follow-up itself.
        { command: reminder, timeout: 10, loop_limit: 1 },
        // Opens/updates the session's chat thread; interactive sessions only (the hook checks).
        { command: notify, timeout: 10 }
      ]
    }
  }, null, 2) + '\n';
}

if (require.main === module) {
  process.stdout.write(hooksConfig());
}

module.exports = { hooksConfig };
