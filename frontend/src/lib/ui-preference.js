export const UI_OPTIONS = [
  { key: 'workspace', label: 'Workspace', hint: 'Conversation, tasks and tools in one view' },
  { key: 'terminal', label: 'Terminal', hint: 'The agent’s own console interface' }
]
const KEY = 'agenthub.preferredUi'

export function preferredUi() {
  try {
    const saved = localStorage.getItem(KEY)
    return UI_OPTIONS.some(option => option.key === saved) ? saved : 'workspace'
  } catch { return 'workspace' }
}

export function savePreferredUi(value) {
  if (!UI_OPTIONS.some(option => option.key === value)) throw new Error('Choose a supported interface.')
  localStorage.setItem(KEY, value)
}
