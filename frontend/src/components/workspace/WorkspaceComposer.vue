<script setup>
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue'
import { workspaceDrafts as drafts, workspaceQueues as queues } from '../../lib/session-drafts.js'
import { completeCommand, leadingCommand, rankCommands, slashQuery } from '../../lib/slash-commands.js'
// enabled: the agent takes input now. queueable: it will soon — the pod is starting or resuming,
// or the agent socket is not connected yet — so a message is held and sent once enabled (#115).
// loadCommands: resolves to the agent's slash commands ({ commands: [...] }); absent, or failing
// (an older runtime, a shared link), the composer offers no completion and still sends.
const props = defineProps({ sessionId: String, enabled: Boolean, queueable: Boolean, disabledHint: String, status: String, agent: String, send: Function, interrupt: Function, loadCommands: Function })
const text = ref(drafts.get(props.sessionId) || '')
const input = ref(null)
const pending = ref(false)
const error = ref('')
const queued = ref(queues.get(props.sessionId) || [])
const accepting = computed(() => props.enabled || props.queueable)
let generation = 0
let flushing = false
function saveQueue(id = props.sessionId) {
  if (queued.value.length) queues.set(id, queued.value)
  else queues.delete(id)
}
watch(() => props.sessionId, (id, previous) => {
  if (previous) { drafts.set(previous, text.value); saveQueue(previous) }
  generation += 1
  commands.value = []
  commandsLoadedAt = 0
  text.value = drafts.get(id) || ''
  queued.value = queues.get(id) || []
  pending.value = false
  error.value = ''
  void flush()
})
// Sends what was queued, oldest first, one at a time: the CLI takes the second message as its
// next prompt only after the first was submitted, and two pastes interleaved in its input box
// would arrive as one garbled turn. An entry leaves the queue only once its send resolved, so
// a reconnect in between retries it rather than losing it — and never sends it twice, because
// a send that failed before writing anything says so (`retryable`). A send that failed after
// it may have reached the terminal goes back to the draft with the reason instead: a person can
// check the terminal, a retry here could not tell whether it would be the second copy.
async function flush() {
  if (flushing || !props.enabled || pending.value || !queued.value.length) return
  flushing = true
  const current = generation
  try {
    while (props.enabled && generation === current && queued.value.length) {
      const [next] = queued.value
      pending.value = true
      try {
        await props.send(next.text)
      } catch (e) {
        if (generation !== current || e?.retryable) return
        queued.value = queued.value.filter(item => item !== next)
        restoreToDraft([next])
        error.value = e?.message || 'Message could not be sent. It is back in your draft.'
        return
      } finally {
        if (generation === current) pending.value = false
      }
      if (generation !== current) return
      queued.value = queued.value.filter(item => item !== next)
    }
  } finally {
    flushing = false
    if (generation === current) saveQueue()
  }
}
watch(() => props.enabled, enabled => { if (enabled) void flush() })
// A queue kept from an earlier mount of this session may meet an agent that is already up.
void flush()
// The session will not take input after all (it failed to start, or was stopped): what was
// queued goes back into the composer rather than vanishing with the queue.
watch(accepting, (now, before) => {
  if (before && !now && queued.value.length) {
    restoreToDraft(queued.value)
    queued.value = []
    saveQueue()
  }
})
function restoreToDraft(items) {
  const parts = [...items.map(item => item.text), text.value].filter(part => part.trim())
  text.value = parts.join('\n\n')
}
function withdraw(item) {
  queued.value = queued.value.filter(entry => entry !== item)
  saveQueue()
}
function editQueued(item) {
  withdraw(item)
  restoreToDraft([item])
  input.value?.focus()
}
async function submit() {
  if (pending.value || !text.value.trim()) return
  if (!props.enabled) {
    if (!props.queueable) return
    // Ids only key the list; they are unique within the session's queue, which outlives a mount.
    const id = Math.max(0, ...queued.value.map(item => item.id)) + 1
    queued.value = [...queued.value, { id, text: text.value }]
    saveQueue()
    text.value = ''
    error.value = ''
    return
  }
  const value = text.value
  const current = generation
  pending.value = true
  error.value = ''
  try {
    await props.send(value)
    if (generation === current && text.value === value) text.value = ''
  } catch (e) { if (generation === current) error.value = e.message || 'Message could not be sent. Your draft is still here.' }
  finally { if (generation === current) pending.value = false }
}
async function stop() {
  try { await props.interrupt() } catch { error.value = 'Could not interrupt. Open the terminal to check the connection.' }
}
// Slash-command completion. The list is fetched when "/" is first typed and again after a
// while, so a skill added during the session shows up without reloading the page.
const COMMANDS_TTL_MS = 30000
const commands = ref([])
const caret = ref(0)
const menuIndex = ref(0)
const dismissed = ref(null)
let commandsLoadedAt = 0
let loadingCommands = null
const query = computed(() => accepting.value && props.loadCommands ? slashQuery(text.value, caret.value) : null)
const matches = computed(() => query.value === null || dismissed.value === text.value.split(/\s/)[0]
  ? [] : rankCommands(commands.value, query.value))
const menuOpen = computed(() => matches.value.length > 0)
const draftCommand = computed(() => leadingCommand(text.value, commands.value))
function ensureCommands() {
  if (loadingCommands || (commandsLoadedAt && Date.now() - commandsLoadedAt < COMMANDS_TTL_MS)) return
  const current = generation
  loadingCommands = Promise.resolve().then(() => props.loadCommands())
    .then(result => {
      if (generation !== current) return
      commands.value = Array.isArray(result?.commands) ? result.commands : []
      commandsLoadedAt = Date.now()
    })
    // A pod still starting answers 503: asked again a few seconds later rather than a full TTL.
    .catch(() => { if (generation === current) commandsLoadedAt = Date.now() - COMMANDS_TTL_MS + 3000 })
    .finally(() => { loadingCommands = null })
}
watch(query, value => { menuIndex.value = 0; if (value !== null) ensureCommands() })
// The list is scrollable; a highlight moved past its edge by the arrow keys is brought into view.
watch(menuIndex, index => {
  void nextTick(() => document.getElementById(`slash-${props.sessionId}-${index}`)?.scrollIntoView?.({ block: 'nearest' }))
})
function trackCaret() { caret.value = input.value?.selectionStart ?? text.value.length }
function choose(command) {
  const completed = completeCommand(text.value, command)
  text.value = completed.text
  caret.value = completed.caret
  menuIndex.value = 0
  void nextTick(() => {
    input.value?.focus()
    input.value?.setSelectionRange(completed.caret, completed.caret)
  })
}
function menuKey(event) {
  const count = matches.value.length
  if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
    menuIndex.value = (menuIndex.value + (event.key === 'ArrowDown' ? 1 : count - 1)) % count
  } else if (event.key === 'Tab' && !event.shiftKey) {
    choose(matches.value[menuIndex.value])
  } else if (event.key === 'Enter' && !event.shiftKey) {
    const command = matches.value[menuIndex.value]
    // The highlighted command is already typed out in full: Enter sends it, as in the CLI.
    if (text.value.trim() === '/' + command.name) {
      dismissed.value = text.value.split(/\s/)[0]
      void submit()
    } else choose(command)
  } else if (event.key === 'Escape') {
    dismissed.value = text.value.split(/\s/)[0]
  } else return false
  event.preventDefault()
  return true
}
function keydown(event) {
  if (menuOpen.value && !event.isComposing && menuKey(event)) return
  if (event.key === 'Enter' && !event.shiftKey && !event.isComposing) {
    event.preventDefault()
    void submit()
  }
}
function quote(value) {
  text.value += `${text.value ? '\n\n' : ''}${String(value).split('\n').map(line => '> ' + line).join('\n')}\n\n`
  input.value?.focus()
}
function clearError() { error.value = '' }
defineExpose({ quote, clearError })
onBeforeUnmount(() => { drafts.set(props.sessionId, text.value); saveQueue(); generation += 1 })
</script>
<template>
  <form class="workspace-composer" @submit.prevent="submit" data-workspace-composer>
    <ol v-if="queued.length" class="composer-queue" aria-label="Queued messages" data-composer-queue>
      <li v-for="item in queued" :key="item.id" data-queued-message>
        <span class="queued-label">Queued · will be sent when the agent is ready</span>
        <span class="queued-text">{{ item.text }}</span>
        <button type="button" data-queued-edit @click="editQueued(item)">Edit</button>
        <button type="button" data-queued-withdraw @click="withdraw(item)">Withdraw</button>
      </li>
    </ol>
    <ul v-if="menuOpen" :id="`slash-menu-${sessionId}`" class="slash-menu" role="listbox" aria-label="Slash commands" data-slash-menu>
      <li v-for="(command, i) in matches" :id="`slash-${sessionId}-${i}`" :key="command.name" role="option"
        :aria-selected="i === menuIndex" :class="{ active: i === menuIndex }" data-slash-option
        @mousedown.prevent="choose(command)" @mousemove="menuIndex = i">
        <span class="slash-name">/{{ command.name }}<span v-if="command.argumentHint" class="slash-hint">{{ command.argumentHint }}</span></span>
        <span class="slash-description">{{ command.description }}</span>
        <span v-if="command.source !== 'builtin'" class="slash-source">{{ command.source }}</span>
      </li>
    </ul>
    <div class="composer-card">
      <textarea ref="input" v-model="text" :disabled="!accepting || pending" rows="3" aria-label="Message the agent"
        placeholder="Give your agent a task… Type / for commands" aria-autocomplete="list" :aria-expanded="menuOpen"
        :aria-controls="menuOpen ? `slash-menu-${sessionId}` : undefined"
        :aria-activedescendant="menuOpen ? `slash-${sessionId}-${menuIndex}` : undefined"
        @keydown="keydown" @input="trackCaret" @click="trackCaret" @keyup="trackCaret"></textarea>
      <div class="composer-controls">
        <span>{{ agent || 'Agent' }} <span class="connection">· {{ status }}</span></span>
        <span class="composer-shortcut">Enter to send · Shift+Enter for newline</span>
        <button type="button" :disabled="!enabled || pending" aria-label="Interrupt agent" @click="stop">◼ Stop</button>
        <button type="submit" class="primary" :disabled="!accepting || pending || !text.trim()">{{ pending ? 'Sending…' : enabled ? '↑ Send' : '↑ Queue' }}</button>
      </div>
    </div>
    <p v-if="disabledHint" class="composer-hint">{{ disabledHint }}</p>
    <p v-else-if="error" class="composer-error" role="alert">{{ error }}</p>
    <p v-else-if="draftCommand?.effect === 'new-conversation'" class="composer-hint warn" data-slash-warning>
      /{{ draftCommand.name }} switches the agent to another conversation. This view keeps showing the current one;
      follow the agent terminal from there.</p>
    <p v-else class="composer-hint">{{ enabled ? 'Messages and slash commands go to the running agent; its menus appear above.'
      : queueable ? 'The agent is starting. Messages are queued and sent once it is ready.'
        : 'Connect to a running interactive session to send a message.' }}</p>
  </form>
</template>
<style scoped>
.workspace-composer { width: 100%; max-width: 904px; margin: 0 auto; padding: 8px 32px 12px; box-sizing: border-box; }
.composer-card { padding: 14px; border: 1px solid var(--border-2); border-radius: 18px; background: var(--panel); box-shadow: 0 6px 24px #0002; }
textarea { width: 100%; min-height: 72px; max-height: 240px; resize: vertical; border: 0; background: transparent; padding: 0; font: 14px/1.6 var(--ui); box-shadow: none; }
textarea:focus { outline: none; }
.composer-card:focus-within { border-color: var(--accent); }
.composer-controls { display: flex; align-items: center; gap: 10px; margin-top: 12px; color: var(--muted); font-size: 11px; }
.composer-controls > span:first-child { margin-right: auto; }
.connection, .composer-shortcut { color: var(--muted-3); }
button { padding: 6px 10px; font-size: 12px; border-radius: 9px; }
.composer-queue { list-style: none; margin: 0 0 8px; padding: 0; display: flex; flex-direction: column; gap: 6px; }
.composer-queue li { display: flex; align-items: center; gap: 8px; padding: 8px 12px; border: 1px dashed var(--border-2); border-radius: 12px; background: var(--panel-2); font-size: 12px; }
.queued-label { color: var(--muted-3); font-size: 10px; white-space: nowrap; }
.queued-text { flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.slash-menu { list-style: none; margin: 0 0 8px; padding: 6px; max-height: 280px; overflow-y: auto; border: 1px solid var(--border-2); border-radius: 14px; background: var(--panel); box-shadow: 0 6px 24px #0003; }
.slash-menu li { display: flex; align-items: baseline; gap: 12px; padding: 6px 10px; border-radius: 9px; cursor: pointer; font-size: 12px; }
.slash-menu li.active { background: var(--panel-2); }
.slash-name { flex-shrink: 0; font-family: var(--mono); color: var(--strong); }
.slash-hint { margin-left: 6px; color: var(--muted-3); }
.slash-description { flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; color: var(--muted); }
.slash-source { color: var(--muted-3); font-size: 10px; }
.composer-hint.warn { color: var(--warn); }
.composer-hint, .composer-error { margin: 8px 0 0; text-align: center; color: var(--muted-3); font-size: 10px; line-height: 1.5; }
.composer-error { color: var(--danger); }
@media(max-width: 640px) { .workspace-composer { padding-inline: 10px; } .composer-shortcut { display: none; } .composer-queue li { flex-wrap: wrap; } .queued-label { width: 100%; } }
</style>
