import { decide, isInteractive, send } from '../common/turn-notify-hook.mjs';

/**
 * End-of-turn chat relay for OpenCode: the counterpart of Codex's Stop and Cursor's stop hook
 * (common/turn-notify-hook.mjs), which only send a "question" when the agent is waiting for its
 * owner. OpenCode has no hook command of that kind; its plugins receive `session.idle`, which
 * fires once the session has finished its turn. Verified on 1.18.35 with a mock model: the event
 * reaches the plugin and the plugin's own client returns the turn's assistant text.
 *
 * Filters:
 * - a session with a parentID is a subagent the `task` tool started; its idle is a step inside
 *   the user's turn, and relaying it would put a half-finished answer in chat;
 * - an aborted last message was interrupted from the keyboard, so someone is already at the
 *   terminal (Cursor's status "aborted" is skipped for the same reason);
 * - unattended modes send nothing, before any lookup: there the end of the turn is the end of
 *   `opencode run`, and the session agent posts Succeeded.
 */
export function createTurnNotifier({ client, env = process.env, sendImpl = send } = {}) {
  return async function onEvent(event) {
    if (!event || event.type !== 'session.idle') return;
    const sessionID = event.properties && event.properties.sessionID;
    if (typeof sessionID !== 'string' || !sessionID) return;
    if (!isInteractive(env)) return;

    const payload = {};
    try {
      const session = await client.session.get({ path: { id: sessionID } });
      if (session && session.data && session.data.parentID) return;
      const messages = await client.session.messages({ path: { id: sessionID } });
      const last = (messages && Array.isArray(messages.data) ? messages.data : [])
        .filter(message => message && message.info && message.info.role === 'assistant').pop();
      if (last) {
        if (last.info.error && last.info.error.name === 'MessageAbortedError') payload.status = 'aborted';
        payload.last_assistant_message = (last.parts || [])
          .filter(part => part && part.type === 'text' && typeof part.text === 'string' && !part.synthetic)
          .map(part => part.text).join('\n');
      }
    } catch {
      // Without the text the generic message is still a correct notification.
    }
    const body = decide(payload, env);
    if (body) await sendImpl(body, env);
  };
}
