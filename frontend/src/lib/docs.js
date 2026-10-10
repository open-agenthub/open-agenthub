// The documentation ships with the project website rather than the app, so these are
// absolute links. Kept in one place so a move only has to be made here.
export const DOCS_BASE = 'https://open-agenthub.github.io/docs'

/** The project repository; the backend sends the same value in /api/config. */
export const REPO_URL = 'https://github.com/open-agenthub/open-agenthub'

/** Label of a build that was never tagged (local image, `npm run dev`). */
export const DEV_VERSION = 'dev'

/**
 * Human label for a deployed version: "v0.12.0" for a release, the bare commit for a branch
 * build, and "dev" when nothing was baked in — so a developer's instance never looks released.
 */
export function versionLabel(version) {
  const v = String(version || '').trim()
  if (!v || v === DEV_VERSION) return DEV_VERSION
  return /^\d/.test(v) ? `v${v}` : v
}

/** Absolute URL of a documentation page, e.g. docsUrl('chat', 'verify-telegram'). */
export function docsUrl(page, anchor) {
  const path = `${DOCS_BASE}/${String(page || '').replace(/^\/+/, '')}`.replace(/\/+$/, '')
  return anchor ? `${path}#${anchor}` : path
}
