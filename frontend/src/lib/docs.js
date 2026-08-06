// The documentation ships with the project website rather than the app, so these are
// absolute links. Kept in one place so a move only has to be made here.
export const DOCS_BASE = 'https://open-agenthub.github.io/docs'

/** Absolute URL of a documentation page, e.g. docsUrl('chat', 'verify-telegram'). */
export function docsUrl(page, anchor) {
  const path = `${DOCS_BASE}/${String(page || '').replace(/^\/+/, '')}`.replace(/\/+$/, '')
  return anchor ? `${path}#${anchor}` : path
}
