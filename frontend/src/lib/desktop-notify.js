// Desktop notifications for session events (unit-tested transition detector +
// thin browser-Notification helpers). Sessions carry the shape stored by
// App.vue's refresh(): { id, title, phase, questionPending, ... }.

/** Phases a session can end in — entering one of them triggers an alert. */
const TERMINAL = { Succeeded: 'finished', Failed: 'failed' }

/**
 * Detects sessions that newly need attention between two polling snapshots.
 * Only sessions present in BOTH snapshots can alert (new sessions on page
 * load never do). Returns [{ id, title, kind: 'question'|'finished'|'failed' }].
 */
export function detectAlerts(prev, next) {
  const before = new Map(prev.map(s => [s.id, s]))
  const alerts = []
  for (const s of next) {
    const old = before.get(s.id)
    if (!old) continue
    if (!old.questionPending && s.questionPending) alerts.push({ id: s.id, title: s.title, kind: 'question' })
    else if (old.phase !== s.phase && Object.hasOwn(TERMINAL, s.phase)) alerts.push({ id: s.id, title: s.title, kind: TERMINAL[s.phase] })
  }
  return alerts
}

const KEY = 'desktopNotify'

/** Whether this browser supports desktop notifications at all. */
export const desktopNotifySupported = () => typeof Notification !== 'undefined'

/** Whether the user opted in to desktop notifications. */
export const desktopNotifyEnabled = () => localStorage.getItem(KEY) === '1'

/**
 * Persists the opt-in and asks the browser for permission when needed.
 * Resolves false when enabling failed because the browser denied permission
 * (or does not support notifications at all).
 */
export async function setDesktopNotify(on) {
  let permission = desktopNotifySupported() ? Notification.permission : 'denied'
  if (on && permission === 'default') permission = await Notification.requestPermission()
  localStorage.setItem(KEY, on ? '1' : '0')
  return !on || permission === 'granted'
}

/** Shows a desktop notification for one alert — only when enabled, permitted,
 *  and the tab is hidden (the in-app badge covers the visible case). */
export function showAlert(alert, onOpen) {
  if (!desktopNotifyEnabled()) return
  if (!desktopNotifySupported() || Notification.permission !== 'granted') return
  if (!document.hidden) return
  const body = {
    question: 'is waiting for your reply',
    finished: 'finished',
    failed: 'failed'
  }[alert.kind]
  if (!body) return
  // Never throw: on some platforms (Chrome on Android, webviews) the page-context
  // constructor throws despite granted permission — the poll loop must survive that.
  try {
    // tag dedupes per session: a newer notification replaces the older one.
    const n = new Notification('Open AgentHub', { body: `Session ${alert.title || alert.id} ${body}.`, tag: `agenthub-${alert.id}` })
    n.onclick = () => { window.focus(); onOpen?.(alert.id); n.close() }
  } catch { /* notifications unavailable here — skip silently */ }
}
