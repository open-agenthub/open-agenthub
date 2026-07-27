<script setup>
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { api, getSharedTranscript, sharedTerminalUrl, terminalUrl } from '../api.js'
import { createChatLog } from '../lib/chat.js'
import { renderMarkdown } from '../lib/markdown.js'

const props = defineProps({ session: Object, active: { type: Boolean, default: true }, readonly: { type: Boolean, default: false }, sharedToken: { type: String, default: null } })
const emit = defineEmits(['status'])

const scroller = ref(null)
const text = ref('')
let ws, reconnectTimer
let disposed = false
let connectionGeneration = 0

const log = createChatLog()
// chat.js stays framework-free; a version counter makes its mutations reactive.
const version = ref(0)
const items = computed(() => { void version.value; return log.items.slice() })
const drafts = computed(() => { void version.value; return log.drafts.filter(d => d.text) })
const busy = computed(() => { void version.value; return log.busy })

const isLive = computed(() => ['Running', 'Pending'].includes(props.session?.phase))
const canSend = computed(() => isLive.value && !props.readonly)

function feed(data) {
  log.feed(data)
  version.value += 1
  scrollToEnd()
}

let scrollQueued = false
function scrollToEnd() {
  if (scrollQueued) return
  scrollQueued = true
  nextTick(() => {
    scrollQueued = false
    const el = scroller.value
    if (el) el.scrollTop = el.scrollHeight
  })
}

function clearReconnect() {
  if (reconnectTimer) clearTimeout(reconnectTimer)
  reconnectTimer = undefined
}

function closeSocket() {
  connectionGeneration += 1
  clearReconnect()
  const socket = ws
  ws = undefined
  if (socket) {
    socket.onclose = null
    socket.onmessage = null
    socket.onerror = null
    socket.onopen = null
    socket.close()
  }
}

async function connect() {
  if (disposed || !isLive.value) return
  clearReconnect()
  const generation = ++connectionGeneration
  const sessionId = props.session.id
  const url = props.sharedToken ? sharedTerminalUrl(props.sharedToken) : await terminalUrl(sessionId)
  if (disposed || generation !== connectionGeneration || sessionId !== props.session.id) return

  const socket = new WebSocket(url)
  ws = socket
  socket.onopen = () => {
    if (disposed || ws !== socket) return
    emit('status', 'connected')
  }
  socket.onmessage = event => {
    if (!disposed && ws === socket && typeof event.data === 'string') feed(event.data)
  }
  socket.onclose = () => {
    if (disposed || ws !== socket) return
    ws = undefined
    emit('status', 'disconnected')
    if (isLive.value) reconnectTimer = setTimeout(connect, 2000)
  }
  socket.onerror = () => {
    if (!disposed && ws === socket) emit('status', 'error')
  }
}

async function transcript() {
  const sessionId = props.session.id
  emit('status', 'history')
  let saved = ''
  try {
    saved = props.sharedToken ? await getSharedTranscript(props.sharedToken) : await api.getTranscript(sessionId)
  } catch { saved = '' }
  if (!disposed && sessionId === props.session.id) feed(saved || '')
}

function submit() {
  const value = text.value.trim()
  if (!value || !canSend.value || ws?.readyState !== WebSocket.OPEN) return
  ws.send(JSON.stringify({ type: 'chat', text: value }))
  text.value = ''
}

function interrupt() {
  if (canSend.value && ws?.readyState === WebSocket.OPEN) ws.send(JSON.stringify({ type: 'interrupt' }))
}

function reconnectForCurrentSession() {
  closeSocket()
  log.reset()
  version.value += 1
  if (isLive.value) connect()
  else transcript()
}

function pretty(value) {
  try { return JSON.stringify(value, null, 2) } catch { return String(value) }
}

function clip(value) {
  return value.length > 4000 ? value.slice(0, 4000) + '\n…' : value
}

onMounted(() => {
  if (isLive.value) connect()
  else transcript()
})

watch(() => props.session.id, reconnectForCurrentSession)
watch(isLive, (live, wasLive) => {
  if (live && !wasLive) connect()
  else if (!live && wasLive) closeSocket()
})

onBeforeUnmount(() => {
  disposed = true
  closeSocket()
})
</script>
<template>
  <div class="pane">
    <div ref="scroller" class="chat-scroll">
      <div class="chat-inner">
        <div v-if="!items.length && !drafts.length" class="empty">
          {{ isLive ? 'Send a message to start the conversation.' : 'No saved conversation.' }}
        </div>
        <template v-for="(item, i) in items" :key="i">
          <div v-if="item.kind === 'user'" class="bubble user" data-chat-user><pre>{{ item.text }}</pre></div>
          <div v-else-if="item.kind === 'assistant'" class="bubble assistant" data-chat-assistant>
            <template v-for="(block, j) in item.blocks" :key="j">
              <div v-if="block.type === 'text'" class="md" v-html="renderMarkdown(block.text)"></div>
              <details v-else-if="block.type === 'thinking'" class="fold thinking">
                <summary>Thinking</summary>
                <div class="md muted-md" v-html="renderMarkdown(block.text)"></div>
              </details>
              <details v-else-if="block.type === 'tool'" class="fold tool" :data-chat-tool="block.name">
                <summary>
                  <span class="tool-name">{{ block.name }}</span>
                  <span v-if="block.isError" class="tool-failed">failed</span>
                  <span v-else-if="block.result === null" class="tool-running">running…</span>
                </summary>
                <pre class="io">{{ pretty(block.input) }}</pre>
                <pre v-if="block.result" class="io result">{{ clip(block.result) }}</pre>
              </details>
            </template>
          </div>
          <div v-else-if="item.kind === 'stderr'" class="note err"><pre>{{ item.text }}</pre></div>
          <div v-else-if="item.kind === 'exit'" class="note">{{ item.text }}</div>
          <div v-else class="note">{{ item.text }}</div>
        </template>
        <div v-if="drafts.length" class="bubble assistant" data-chat-draft>
          <template v-for="draft in drafts" :key="draft.index">
            <details v-if="draft.type === 'thinking'" class="fold thinking" open>
              <summary>Thinking…</summary>
              <div class="md muted-md" v-html="renderMarkdown(draft.text)"></div>
            </details>
            <div v-else class="md" v-html="renderMarkdown(draft.text)"></div>
          </template>
        </div>
        <div v-else-if="busy" class="typing" data-chat-busy><span></span><span></span><span></span></div>
      </div>
    </div>
    <div v-if="canSend" class="composer">
      <textarea v-model="text" data-chat-input rows="1" placeholder="Message the agent — Enter to send, Shift+Enter for a new line"
        @keydown.enter.exact.prevent="submit"></textarea>
      <button v-if="busy" class="stop" data-chat-stop @click="interrupt">◼ Stop</button>
      <button class="primary" data-chat-send :disabled="!text.trim()" @click="submit">Send</button>
    </div>
  </div>
</template>
<style scoped>
.pane { flex: 1; display: flex; flex-direction: column; min-width: 0; min-height: 0; background: #0e0d0b; }
.chat-scroll { flex: 1; overflow-y: auto; min-height: 0; }
.chat-inner { max-width: 760px; margin: 0 auto; padding: 22px 24px 28px; display: flex; flex-direction: column; gap: 14px; }
.empty { color: var(--muted-3); font-size: 13px; padding: 24px 0; text-align: center; }
.bubble { border-radius: var(--radius-lg); font-size: 14px; line-height: 1.6; }
.bubble.user { align-self: flex-end; max-width: 85%; background: var(--panel-2); border: 1px solid var(--border-2); padding: 10px 14px; }
.bubble.user pre { margin: 0; white-space: pre-wrap; word-break: break-word; font: 13px/1.6 var(--ui); color: var(--text); }
.bubble.assistant { align-self: stretch; color: var(--text); display: flex; flex-direction: column; gap: 8px; }
.md :deep(p) { margin: 0 0 8px; }
.md :deep(p:last-child) { margin-bottom: 0; }
.md :deep(h3), .md :deep(h4), .md :deep(h5) { margin: 10px 0 6px; font-size: 15px; }
.md :deep(ul), .md :deep(ol) { margin: 0 0 8px; padding-left: 22px; }
.md :deep(code) { background: var(--input); border: 1px solid var(--border); border-radius: 5px; padding: 1px 5px; font: 12px var(--mono); }
.md :deep(pre.md-code) { background: var(--input); border: 1px solid var(--border); border-radius: var(--radius); padding: 10px 12px; overflow-x: auto; margin: 0 0 8px; }
.md :deep(pre.md-code code) { background: none; border: none; padding: 0; font: 12.5px/1.55 var(--mono); color: #c9c4bb; }
.muted-md { color: var(--muted-2); font-size: 13px; }
.fold { border: 1px solid var(--border); border-radius: var(--radius); background: var(--panel); }
.fold summary { display: flex; align-items: center; gap: 8px; padding: 7px 12px; font-size: 12px; font-weight: 600; color: var(--muted-2); cursor: pointer; user-select: none; }
.fold summary:hover { color: var(--text); }
.fold[open] summary { border-bottom: 1px solid var(--border); }
.fold > .md, .fold > .io { padding: 10px 12px; }
.tool-name { font-family: var(--mono); color: var(--accent); }
.tool-running { color: var(--warn); font-weight: 400; }
.tool-failed { color: var(--danger); font-weight: 700; }
.io { margin: 0; white-space: pre-wrap; word-break: break-word; font: 12px/1.55 var(--mono); color: var(--muted); max-height: 320px; overflow-y: auto; }
.io.result { border-top: 1px dashed var(--border); color: #c9c4bb; }
.note { align-self: center; color: var(--muted-3); font-size: 12px; }
.note.err { align-self: stretch; color: var(--danger); }
.note.err pre { margin: 0; white-space: pre-wrap; word-break: break-word; font: 12px/1.5 var(--mono); }
.typing { display: flex; gap: 5px; padding: 4px 2px; }
.typing span { width: 7px; height: 7px; border-radius: 50%; background: var(--muted-3); animation: pulse 1.2s infinite ease-in-out; }
.typing span:nth-child(2) { animation-delay: 0.15s; }
.typing span:nth-child(3) { animation-delay: 0.3s; }
@keyframes pulse { 0%, 80%, 100% { opacity: 0.25; } 40% { opacity: 1; } }
.composer { display: flex; gap: 10px; padding: 12px 16px; background: var(--bg); border-top: 1px solid var(--border); }
.composer textarea { flex: 1; background: var(--hover); border: 1px solid var(--border-2); border-radius: var(--radius); font-family: var(--ui); font-size: 14px; min-height: 42px; max-height: 180px; resize: vertical; }
.composer button { align-self: flex-end; padding: 9px 18px; white-space: nowrap; }
.composer .stop { color: var(--danger); border-color: var(--border-3); }
</style>
