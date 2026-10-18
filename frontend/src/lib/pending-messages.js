// What the Workspace shows below the conversation as pending (#153): messages that were sent to
// the agent but that its CLI has not taken yet, and fleet messages it has not received yet. A
// CLI that is working holds a message typed into it in a queue of its own — Claude Code 2.1.287
// ("Press up to edit queued messages") and Codex 0.160.0 ("Messages to be submitted after next
// tool call") — and writes the user turn to its transcript only when it takes it, which the
// native conversation then shows. That entry is the signal; the terminal gives no other.

// The header the session agent types in front of a fleet message and the Claude fleet mod puts
// in front of one it submits (common/terminal-inject.js, mods/agenthub-fleet/hooks/lib.mjs), so a
// transcript reads the same whichever channel delivered it.
const FLEET_HEADER = /^\[AgentHub message from (?:agent "(.*)" \(([A-Za-z0-9-]+)\)|outside the fleet)\]\n?/
// Claude Code 2.1.287 records a prompt a plugin submitted wrapped in two lines of its own: "The
// agenthub-fleet plugin sent a message:" before it and a paragraph for the model after it.
const PLUGIN_PREAMBLE = /^The [\w.-]+ plugin sent a message:\n/
const PLUGIN_NOTE = /\n\nThis is how Claude Code surfaces a prompt a plugin submits[\s\S]*$/

export function parseFleetMessage(text) {
  let value = String(text ?? '')
  const wrapped = PLUGIN_PREAMBLE.exec(value)
  if (wrapped) value = value.slice(wrapped[0].length).replace(PLUGIN_NOTE, '')
  const match = FLEET_HEADER.exec(value)
  if (!match) return null
  return { fromTitle: match[1] ?? null, from: match[2] ?? null, body: value.slice(match[0].length) }
}

export function fleetLabel(message) {
  return message.from ? `Message from agent “${message.fromTitle || message.from}”` : 'Message from outside the fleet'
}

// The CLIs keep a pasted message's text, but line endings, indentation of a wrapped paste and
// trailing blanks can differ from what the composer held.
export function normalizeMessage(text) {
  return String(text ?? '').replace(/[\x00-\x1f\x7f]+/g, ' ').replace(/\s+/g, ' ').trim()
}

function occurrences(items, text) {
  const wanted = normalizeMessage(text)
  if (!wanted) return 0
  return items.filter(item => item.role === 'user' && normalizeMessage(item.text).includes(wanted)).length
}

// Whether a sent message can be followed into the conversation at all. A slash command is the
// CLI's own business — Codex writes none of them to its rollout — and a terminal-history
// conversation has no user turns to find it in; neither would ever leave the queue.
export function trackable(text, source) {
  return source === 'native' && !!normalizeMessage(text) && !String(text).trimStart().startsWith('/')
}

// A sent message counts as taken once the conversation holds more user turns with its text than
// it did when the message was sent; "continue" sent twice is two messages, not one.
export function sentEntry(id, text, items) {
  return { id, text, baseline: occurrences(items, text) }
}

export function stillPending(sent, items) {
  return sent.filter(entry => occurrences(items, entry.text) <= entry.baseline)
}

// A fleet message is pending until the hub records it as delivered, through whichever channel
// (inbox read, typed into the terminal, the mod, or at the end of the running turn). One the
// pod accepted for "when the running turn ends" (#173) is left unmarked until it is handed over,
// so an undelivered row may be in the inbox or queued for the turn's end, and the label claims
// neither.
export function pendingFleetMessages(messages) {
  return (messages || []).filter(message => !message.deliveredAt)
}

export const FLEET_WAITING = 'not yet received by the agent'
