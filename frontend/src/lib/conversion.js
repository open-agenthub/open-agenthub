// Pure helpers for continuing an autonomous session interactively (docs/session-mode-conversion.md).

/** Runtimes whose CLI resumes the same conversation after the mode flips. */
const KEEPS_CONVERSATION = new Set(['Claude', 'Codex'])

export function keepsConversation(agent) {
  return KEEPS_CONVERSATION.has(agent)
}

/** The one-line hint under the card's buttons, so the person knows what they get. */
export function conversionHint(agent) {
  return keepsConversation(agent)
    ? `${agent} keeps the conversation — the agent continues where the run stopped.`
    : `${agent || 'This runtime'} starts a new conversation in the same workspace; the files stay, the chat history does not.`
}

/** The chat pane is interactive Claude only, the same rule the backend applies. */
export function canOpenChat(session) {
  return session?.agent === 'Claude'
}

/** Whether the card belongs on this session: the backend's own predicate, never re-derived here. */
export function canConvert(session) {
  return session?.canConvertToInteractive === true
}

/** Label for the header once a session was converted; null if it never was. */
export function convertedLabel(session) {
  const from = session?.convertedFrom
  return from ? `converted from ${String(from).toLowerCase()}` : null
}

/**
 * The text of a failed convert call. api.js throws `<status> <body>` and the convert endpoints
 * answer with `{ error }`, so the body is unwrapped; anything else is shown as it came.
 */
export function conversionErrorText(error) {
  const message = error instanceof Error ? error.message : error
  const raw = String(message ?? '').replace(/^\d{3}\s*/, '')
  try {
    const parsed = JSON.parse(raw)
    if (parsed && typeof parsed.error === 'string') return parsed.error
  } catch {}
  return raw || 'The session could not be converted.'
}
