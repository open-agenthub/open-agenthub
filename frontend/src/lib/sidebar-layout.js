export const SIDEBAR_DEFAULT = 248
// Narrower than this the brand row cuts its own name and the collapse button off (measured: 228px).
export const SIDEBAR_MIN = 232
export const SIDEBAR_MAX = 480
const KEY = 'agenthub.sidebar'

export function clampSidebarWidth(width) {
  const value = Math.round(Number(width))
  if (!Number.isFinite(value)) return SIDEBAR_DEFAULT
  return Math.min(SIDEBAR_MAX, Math.max(SIDEBAR_MIN, value))
}

// The layout is a per-viewer, per-device choice, so the browser keeps it rather than the account:
// a width dragged out on a wide monitor would be wrong on a laptop. Storage that throws or holds
// garbage (private mode, a hand-edited value) yields the default instead of a broken shell.
export function loadSidebarLayout() {
  try {
    const saved = JSON.parse(localStorage.getItem(KEY) || 'null')
    if (!saved || typeof saved !== 'object') return { width: SIDEBAR_DEFAULT, collapsed: false }
    return { width: clampSidebarWidth(saved.width ?? SIDEBAR_DEFAULT), collapsed: saved.collapsed === true }
  } catch { return { width: SIDEBAR_DEFAULT, collapsed: false } }
}

export function saveSidebarLayout({ width, collapsed }) {
  try { localStorage.setItem(KEY, JSON.stringify({ width: clampSidebarWidth(width), collapsed: !!collapsed })) } catch {}
}
