// The mod's logic without the mods API: what a message looks like once it is in the prompt,
// what the status line and /inbox say, and which messages a poll should submit, abort for,
// or hold. Kept apart from register.js so the session-agent suite can test it with node:test;
// the hooks module itself only runs under `claude plugin test`.

/** The prompt text a fleet message becomes — the same header the session agent types into a
 * terminal, so a transcript reads the same whichever channel delivered it. */
export function formatFleetMessage(message) {
  const header = message.from
    ? '[AgentHub message from agent "' + (message.fromTitle || message.from) + '" (' + message.from + ')]'
    : '[AgentHub message from outside the fleet]';
  return header + '\n' + message.body;
}

/** Reads the session agent's inbox answer defensively: anything that is not a message with a
 * non-empty body is dropped rather than submitted as a prompt. */
export function parseInbox(text) {
  let parsed;
  try { parsed = JSON.parse(text); } catch { return []; }
  const list = parsed && Array.isArray(parsed.messages) ? parsed.messages : [];
  return list
    .filter(entry => entry && typeof entry === 'object' && typeof entry.body === 'string' && entry.body.trim())
    .map(entry => ({
      id: typeof entry.id === 'string' ? entry.id : '',
      from: typeof entry.from === 'string' && entry.from ? entry.from : null,
      fromTitle: typeof entry.fromTitle === 'string' && entry.fromTitle ? entry.fromTitle : null,
      body: entry.body,
      priority: entry.priority === true,
      interrupt: entry.interrupt === true
    }));
}

/**
 * Sorts one poll's messages into what happens now and what waits. `abort` is true when any
 * priority message asks for an interrupt while a turn runs — one abort serves them all, since
 * the turn is gone after the first. Priority messages are submitted in arrival order; the rest
 * join the waiting list for the status line and /inbox.
 */
export function planDelivery(messages, turnRunning) {
  const submit = messages.filter(message => message.priority);
  const waiting = messages.filter(message => !message.priority);
  const abort = turnRunning && submit.some(message => message.interrupt);
  return { submit, waiting, abort };
}

export function inboxStatus(count) {
  return '📨 ' + count + ' fleet message' + (count === 1 ? '' : 's') + ' — /inbox';
}

/** What /inbox prints and what a prompt.submit hook attaches: every waiting message, headed. */
export function inboxText(messages) {
  if (!messages.length) return 'No fleet messages are waiting.';
  return messages.map(formatFleetMessage).join('\n\n');
}
