// Keep unsent text across keyed session view remounts. Memory only: a reload or
// sign-out clears drafts, and chat attachments keep their separate lifecycle.
export const workspaceDrafts = new Map()
export const chatDrafts = new Map()
// Messages written while the session was still starting, per session, in the order they were
// queued; WorkspaceComposer sends them once the agent is connected (#115).
export const workspaceQueues = new Map()
