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

/** Display status for a session row: phases mapped to the design's vocabulary,
 *  with a pending question surfacing as "Waiting". */
export function sessionStatus(session) {
  if (!session) return 'Unknown'
  if (session.questionPending) return 'Waiting'
  return { Succeeded: 'Done', Pending: 'Starting' }[session.phase] || session.phase || 'Unknown'
}

/** Badge foreground/background per display status. */
export const STATUS_STYLES = {
  Running:   { color: 'var(--running)', bg: 'rgba(95,214,139,0.1)' },
  Starting:  { color: 'var(--accent)', bg: 'rgba(90,169,245,0.1)' },
  Waiting:   { color: 'var(--warn)', bg: 'rgba(245,197,90,0.12)' },
  Scheduled: { color: 'var(--sched)', bg: 'rgba(167,139,250,0.1)' },
  Paused:    { color: 'var(--paused)', bg: 'rgba(163,158,150,0.1)' },
  Done:      { color: 'var(--accent)', bg: 'rgba(90,169,245,0.1)' },
  Failed:    { color: 'var(--danger)', bg: 'rgba(245,122,106,0.1)' }
}

export function statusStyle(session) {
  return STATUS_STYLES[sessionStatus(session)] || { color: 'var(--muted)', bg: 'rgba(163,158,150,0.1)' }
}
