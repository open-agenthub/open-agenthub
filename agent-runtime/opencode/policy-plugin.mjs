import { report as reportTurn } from '../common/fleet-turn-hook.mjs';
import fs from 'node:fs';
import path from 'node:path';
import { createGate } from './policy-gate.mjs';
import { createLimitNotice } from './limit-notice.mjs';
import { createTurnNotifier } from './turn-notify.mjs';
import { createNextTurnRelay } from './next-turn.mjs';
import { installSkillRefresher } from './skill-refresh.mjs';

// OpenCode treats every function this module exports as a plugin and calls it at startup, so
// this file exports the plugin and nothing else; the logic lives in policy-gate.mjs,
// turn-notify.mjs, next-turn.mjs, limit-notice.mjs and skill-refresh.mjs.
export const AgentHubPolicy = async (input) => {
  const inputClient = input && input.client;
  const gate = createGate();
  const notify = createTurnNotifier({ client: input && input.client });
  const nextTurn = createNextTurnRelay({ client: input && input.client });
  const limitNotice = createLimitNotice();
  const skills = installSkillRefresher({ client: input && input.client });
  return {
    // The CLI creates its own session IDs; recording the submitted conversation prevents
    // --continue from selecting a shell-tab session when this pod resumes.
    'chat.message': async (input) => {
      await nextTurn.onChatMessage().catch(() => {});
      if (typeof input?.sessionID !== 'string' || !/^ses_[A-Za-z0-9]+$/.test(input.sessionID)) return;
      try {
        const session = await inputClient.session.get({ path: { id: input.sessionID } });
        if (!session?.data || session.data.parentID) return;
        await reportTurn('prompt');
        const root = path.join(process.env.HOME || '/home/agent', '.opencode');
        fs.mkdirSync(root, { recursive: true });
        fs.writeFileSync(path.join(root, 'agenthub-session-id'), input.sessionID + '\n');
      } catch {}
    },
    'tool.execute.before': async (input, output) => {
      await gate(input && input.tool, output && output.args);
    },
    // A failed relay must never disturb the session, so nothing here throws.
    event: async ({ event }) => {
      try { limitNotice(event); } catch {}
      try { skills.onEvent(event); } catch {}
      if (event?.type === 'session.idle') {
        try {
          const sessionID = event.properties?.sessionID;
          const session = sessionID && await inputClient.session.get({ path: { id: sessionID } });
          if (session?.data && !session.data.parentID) await reportTurn('stop');
        } catch {}
      }
      await notify(event).catch(() => {});
      await nextTurn.onEvent(event).catch(() => {});
    }
  };
};
