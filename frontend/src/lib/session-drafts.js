// Keep unsent text across keyed session view remounts. Memory only: a reload or
// sign-out clears drafts, and chat attachments keep their separate lifecycle.
export const workspaceDrafts = new Map()
export const chatDrafts = new Map()
