// The agent CLIs run a TUI on the PTY and treat a chunk of text as a paste.
// A carriage return inside the same chunk is consumed as part of that paste,
// so the submit lands before the pasted text is applied and the message stays
// sitting in the CLI's input box until the user presses Enter again. Submitting
// therefore sends the text first and the Enter as its own delayed chunk.
export const SUBMIT_ENTER_DELAY_MS = 150

export function submitToAgent(send, text, schedule = (fn, ms) => setTimeout(fn, ms)) {
  if (!text) return false
  send({ type: 'input', data: text })
  schedule(() => send({ type: 'input', data: '\r' }), SUBMIT_ENTER_DELAY_MS)
  return true
}
