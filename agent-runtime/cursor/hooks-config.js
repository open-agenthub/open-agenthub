'use strict';

// Cursor's hooks.json (cursor.com/docs/hooks). Only the skill reminder lives here: the
// permission policy is enforced through cli-config.json, not hooks.
//
// Cursor's stop hook is the same moment as Claude's and Codex's, and its payload carries
// neither a transcript nor a tool name — so the two marker hooks below are what tell the
// reminder whether the turn actually changed anything.

function hooksConfig(runtime = process.env.RUNTIME || '/opt/session-agent') {
  const reminder = `node ${runtime}/common/skill-reminder-hook.mjs`;
  return JSON.stringify({
    version: 1,
    hooks: {
      afterFileEdit: [{ command: `${reminder} --mark work`, timeout: 10 }],
      beforeShellExecution: [{ command: `${reminder} --mark work`, timeout: 10 }],
      // loop_limit on top of the hook's own loop_count check: one reminder per turn is the
      // whole intent, and Cursor auto-submits the follow-up itself.
      stop: [{ command: reminder, timeout: 10, loop_limit: 1 }]
    }
  }, null, 2) + '\n';
}

if (require.main === module) {
  process.stdout.write(hooksConfig());
}

module.exports = { hooksConfig };
