// Pure helpers for session status + terminal-tab display (unit-tested).

/** Dot / badge colour per session phase (CSS custom properties). */
export const PHASE_COLORS = {
  Running: 'var(--running)',
  Pending: 'var(--accent)',
  Scheduled: 'var(--muted)',
  Succeeded: 'var(--ok)',
  Failed: 'var(--danger)',
  Paused: 'var(--paused)'
}

/** Colour for a phase, falling back to the muted colour for unknown phases. */
export function phaseColor(phase) {
  return PHASE_COLORS[phase] || 'var(--muted)'
}

/** A running/starting interactive or autonomous session can be paused. */
export function canPause(session) {
  if (!session || session.mode === 'Scheduled') return false
  return session.phase === 'Running' || session.phase === 'Pending'
}

/** Labels for the terminal tabs. */
export const TAB_LABELS = { agent: 'Agent', shell: 'Shell' }

/** Human label for a terminal tab kind. */
export function tabLabel(kind) {
  return TAB_LABELS[kind] || kind
}
