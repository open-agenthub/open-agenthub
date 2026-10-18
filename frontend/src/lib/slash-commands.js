// Slash-command completion for the Workspace composer (#169, docs/slash-commands.md). The list
// comes from the session agent; executing a command is the CLI's job — the composer sends the
// text into the terminal exactly as for any other message.

const SOURCE_ORDER = { builtin: 0, custom: 1, skill: 2 }

// The "/" token being typed, or null. Only at the very start of the message: every CLI here
// expands a command only when the input begins with it, so offering one mid-text would
// suggest something that is then sent to the model as plain words.
export function slashQuery(text, caret = text?.length ?? 0) {
  if (typeof text !== 'string' || !text.startsWith('/')) return null
  const token = /^\/\S*/.exec(text)[0]
  // Once the caret has left the token (arguments are being typed) the menu has done its job.
  if (caret < 1 || caret > token.length) return null
  return token.slice(1)
}

// The command the message starts with, when it is one the list knows.
export function leadingCommand(text, commands) {
  const match = /^\/(\S+)/.exec(text || '')
  if (!match) return null
  return commands.find(command => command.name === match[1]) || null
}

function score(command, query) {
  const name = command.name.toLowerCase()
  if (!query) return 4
  if (name === query) return 0
  if (name.startsWith(query)) return 1
  if (name.split(/[-_:.]/).some(part => part.startsWith(query))) return 2
  if (name.includes(query)) return 3
  if ((command.description || '').toLowerCase().includes(query)) return 5
  return null
}

// Unbounded beyond what the session agent sends (400): a cut here would hide the last commands of
// an empty "/" list, which is scrolled rather than filtered.
export function rankCommands(commands, query, limit = 400) {
  const needle = (query || '').toLowerCase()
  return commands
    .map(command => ({ command, rank: score(command, needle) }))
    .filter(entry => entry.rank !== null)
    .sort((a, b) => a.rank - b.rank ||
      (SOURCE_ORDER[a.command.source] ?? 3) - (SOURCE_ORDER[b.command.source] ?? 3) ||
      a.command.name.localeCompare(b.command.name))
    .slice(0, limit)
    .map(entry => entry.command)
}

// Replaces the typed token with the full command and a space, so the arguments go right after
// it; the rest of the message is kept.
export function completeCommand(text, command) {
  const rest = text.replace(/^\/\S*/, '').replace(/^\s+/, '')
  const head = `/${command.name} `
  return { text: head + rest, caret: head.length }
}
