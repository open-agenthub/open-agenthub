// Switching a running session's model from the Workspace (#478, docs/design/live-model-switch.md).
// The hub answers how a pick applies (`switchMode` on the session's model catalog) and either
// confirms the switch or names why not; everything here only turns that into what the picker says.

/** What a pick does, in the words the picker shows next to its button. */
export function switchModeNote(mode, agent) {
  if (mode === 'live') {
    return 'Switches the running conversation between turns; the agent keeps its context.'
  }
  if (mode === 'restart') {
    const why = agent === 'Cursor'
      ? ' Cursor’s own picker could not be driven from here.'
      : agent === 'Codex' ? ' Codex accepts only its own model ids while it runs.'
        : agent === 'Claude' ? ' A chat-mode session has no command line to switch in.' : ''
    return `Saved and used from the next restart; the conversation resumes on it.${why}`
  }
  return 'This session’s model cannot be changed here.'
}

/**
 * Why a switch would be refused right now, from what the Workspace already knows about the
 * agent's turn — so the button says so before anyone presses it. The session agent checks again
 * when the switch arrives; this only saves the round trip.
 */
export function busyReason(activity, { pendingPermissions = 0, menuOpen = false } = {}) {
  if (pendingPermissions > 0 || (activity?.state === 'waiting' && activity?.reason === 'approval')) return 'approval_open'
  if (activity?.state === 'waiting' || menuOpen) return 'question_open'
  if (activity?.state === 'working') return 'turn_running'
  return ''
}

const REASONS = {
  turn_running: 'The agent is working. Switch once its turn has ended.',
  question_open: 'A question is open. Answer it first, then switch.',
  approval_open: 'An approval is waiting. Decide it first, then switch.',
  menu_open: 'A menu is open in the agent terminal. Close it first.',
  draft_in_prompt: 'Text was typed into the agent terminal and not sent. Send or clear it (Ctrl+U), then switch.',
  message_typing: 'A message is being typed into the agent. Try again in a moment.',
  context_clear_pending: 'The agent is clearing its context. Try again once the fresh conversation has started.',
  model_switch_pending: 'Another switch is already under way.',
  agent_starting: 'The agent is still starting. Try again in a moment.',
  agent_not_running: 'The agent is not running. Start the session, then switch.',
  session_not_running: 'The session is not running. This agent switches only in a running conversation; start it, then switch.',
  agent_restarted: 'The agent restarted during the switch. Check which model it runs on, then try again.',
  model_refused: 'The agent refused this model.',
  model_switch_not_confirmed: 'The agent did not confirm the switch. Check the agent terminal for what it shows.',
  model_picker_not_shown: 'The agent’s model picker did not open. Check the agent terminal.',
  model_switch_wrong_model: 'The agent switched to another model than the one picked.',
  model_not_in_catalog: 'This model is not in the session’s list. Refresh the list, or pick one from it.',
  model_name_ambiguous: 'Two models in the list share this name, so the agent’s picker cannot tell them apart. Switch in the agent terminal.',
  model_switch_unsupported: 'This session’s model cannot be changed here.',
  model_switch_not_allowed: 'Only the owner and collaborators can switch the model.',
  runtime_too_old: 'This session runs an older runtime without live switching. Update its runtime image, or change the model in Edit session for the next start.',
  invalid_model: 'That is not a model id: no spaces, at most 200 characters, not starting with “-”.',
  model_switch_unavailable: 'The session’s pod did not answer. Try again in a moment.'
}

/** The sentence for a refusal code, with the agent's own line when it sent one. */
export function switchErrorText(code, detail) {
  const text = REASONS[code] || 'The switch failed.'
  if (!detail) return text
  if (code === 'model_switch_wrong_model') return `The agent switched to “${detail}” instead of the model picked; that is the model it now uses.`
  return `${text} It said: “${detail}”`
}

/** The model shown as current: a switch confirmed in this view, else the stored one, else the CLI's default. */
export function currentModel(view, confirmed) {
  return confirmed || view?.selected || ''
}

/** A model id the hub would take; the same rule it applies (AgentConfiguration.IsValidModelName). */
export function validModelId(value) {
  const id = String(value || '').trim()
  return id.length > 0 && id.length <= 200 && !/[\s\u0000-\u001f\u007f-\u009f]/.test(id) && !id.startsWith('-')
}
