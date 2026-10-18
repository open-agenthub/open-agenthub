import { isInteractive, report } from '../common/fleet-turn-hook.mjs';

/**
 * Fleet messages as the next turn for OpenCode (#192, docs/priority-messages.md): the counterpart
 * of the managed Codex hooks that run common/fleet-turn-hook.mjs. OpenCode has no hook command
 * that can return input at the end of a turn, but its plugin has the client the TUI itself
 * talks to, so the text the session agent answers a Stop with is submitted as a new prompt of the
 * same session (`session.promptAsync`). Like the Codex Stop hook, nothing is typed.
 *
 * - `chat.message` (a prompt was submitted, the person's or this plugin's) reports "prompt";
 * - a top-level `session.idle` reports "stop" and submits the answer, if any.
 *
 * Filters, as in turn-notify.mjs: a session with a parentID is a subagent the `task` tool ran, its
 * idle is a step inside the user's turn; an aborted turn was stopped from the keyboard, and
 * someone who just pressed Escape should not find a peer's message started in its place — it
 * waits for the next turn's end. A turn that failed with an error waits too: the person has
 * something to look at first. Approvals need no report: the managed config sets OpenCode's
 * own permission to "allow" and the policy plugin asks the hub, so the TUI never opens an approval
 * dialog a message could be typed into.
 */
export function createNextTurnRelay({ client, env = process.env, reportImpl = report } = {}) {
  async function topLevelIdle(sessionID) {
    const session = await client.session.get({ path: { id: sessionID } });
    if (!session || !session.data || session.data.parentID) return false;
    const messages = await client.session.messages({ path: { id: sessionID } });
    const last = (messages && Array.isArray(messages.data) ? messages.data : [])
      .filter(message => message && message.info && message.info.role === 'assistant').pop();
    // Measured on 1.18.35: when Escape stops a turn, session.idle fires before the abort is stored,
    // so the last answer carries neither MessageAbortedError nor a completion time yet. A turn
    // that ended on its own always has both `time.completed` and no error.
    return Boolean(last && last.info.time && last.info.time.completed && !last.info.error);
  }

  return {
    async onChatMessage() {
      if (!isInteractive(env)) return;
      await reportImpl('prompt', env);
    },

    async onEvent(event) {
      if (!event || event.type !== 'session.idle') return;
      const sessionID = event.properties && event.properties.sessionID;
      if (typeof sessionID !== 'string' || !sessionID || !isInteractive(env)) return;
      if (!(await topLevelIdle(sessionID))) return;
      const text = await reportImpl('stop', env);
      if (!text) return;
      await client.session.promptAsync({ path: { id: sessionID }, body: { parts: [{ type: 'text', text }] } });
    }
  };
}
