// Session CPU/memory requests and limits (docs/session-resources.md). The backend validates
// and clamps; these helpers only shape the dialogs' fields and payloads.

/** Dialog fields for a session (or the defaults of a new one). An empty limit = instance default. */
export function resourceForm(session) {
  return {
    cpu: session?.cpu || '500m',
    memory: session?.memory || '1Gi',
    cpuLimit: session?.cpuLimit || '',
    memoryLimit: session?.memoryLimit || '',
  }
}

/** The four fields as the API takes them; "" for a limit asks for the instance default. */
export function resourcePayload(form) {
  return {
    cpu: String(form.cpu || '').trim(),
    memory: String(form.memory || '').trim(),
    cpuLimit: String(form.cpuLimit || '').trim(),
    memoryLimit: String(form.memoryLimit || '').trim(),
  }
}

/** Whether the dialog asks for other resources than the session has stored. */
export function resourcesChanged(form, session) {
  const next = resourcePayload(form)
  const stored = resourcePayload(resourceForm(session))
  return Object.keys(next).some(key => next[key] !== stored[key])
}

/**
 * Whether saving these resources can restart the session at once: only a running interactive
 * session is restarted (pause and resume). An autonomous one would rerun its prompt, and a
 * paused or finished one gets them at its next start anyway.
 */
export function canApplyNow(session) {
  return session?.mode === 'Interactive' && session?.phase === 'Running'
}

/** "500m / 2" style text of a session's effective request and limit, or '' when unknown. */
export function resourceSummary(resources) {
  if (!resources) return ''
  return `CPU ${resources.cpuRequest} / ${resources.cpuLimit} · memory ${resources.memoryRequest} / ${resources.memoryLimit}`
}
