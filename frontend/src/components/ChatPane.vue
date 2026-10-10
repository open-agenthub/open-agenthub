<script setup>
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { api, getSharedTranscript, sharedTerminalUrl, terminalUrl } from '../api.js'
import { createAttachmentQueue } from '../lib/attachments.js'
import { createChatLog } from '../lib/chat.js'
import { renderMarkdown } from '../lib/markdown.js'
import { renderMermaidBlocks } from '../lib/mermaid.js'
import ChatAttachments from './ChatAttachments.vue'

const props = defineProps({ session: Object, active: { type: Boolean, default: true }, readonly: { type: Boolean, default: false }, sharedToken: { type: String, default: null } })
const emit = defineEmits(['status'])

const scroller = ref(null)
const text = ref('')
const fileInput = ref(null)
let ws, reconnectTimer
const pendingTurn = ref(null)
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
const canAttach = computed(() => canSend.value && !pendingTurn.value && !props.sharedToken)
const attachmentVersion = ref(0)
let attachmentQueue = makeAttachmentQueue()
const attachmentItems = computed(() => {
  void attachmentVersion.value
  return attachmentQueue.items.slice()
})
const readyAttachments = computed(() => attachmentItems.value.filter(item => item.state === 'ready'))
const attachmentsSettled = computed(() => attachmentItems.value.every(item => item.state === 'ready'))
const canSubmit = computed(() => canSend.value && !pendingTurn.value && attachmentsSettled.value &&
  Boolean(text.value.trim() || readyAttachments.value.length))

function makeAttachmentQueue() {
  return createAttachmentQueue({
    sessionId: props.session?.id,
    api,
    onChange: () => { attachmentVersion.value += 1 }
  })
}

function handleDeliveryFrame(data) {
  for (const line of String(data).split('\n')) {
    let event
    try { event = JSON.parse(line) } catch { continue }
    if (event?.type !== 'agenthub' || event.clientTurnId !== pendingTurn.value?.id) continue
    if (event.subtype === 'chat_delivered') {
      if (text.value === pendingTurn.value.text) text.value = ''
      attachmentQueue.clearReady()
      pendingTurn.value = null
    } else if (event.subtype === 'error' && event.code === 'attachment_delivery_failed') {
      pendingTurn.value = null
    }
  }
}

function feed(data) {
  handleDeliveryFrame(data)
  log.feed(data)
  version.value += 1
  scrollToEnd()
}

// Follow new output only while the reader is already at the bottom. Every streamed frame
// calls feed(), so pinning unconditionally yanked the view back down several times a second:
// on a phone the user could neither read earlier output nor operate the chat. The flag is
// updated from the reader's own scroll events, not measured at update time, so a large chunk
// arriving in one frame cannot be mistaken for the reader having scrolled away.
const STICK_THRESHOLD_PX = 48
let stickToBottom = true
function onScroll() {
  const el = scroller.value
  if (!el) return
  stickToBottom = el.scrollHeight - el.scrollTop - el.clientHeight <= STICK_THRESHOLD_PX
}

let scrollQueued = false
function scrollToEnd({ force = false } = {}) {
  if (force) stickToBottom = true
  if (scrollQueued) return
  scrollQueued = true
  nextTick(() => {
    scrollQueued = false
    const el = scroller.value
    if (el && stickToBottom) el.scrollTop = el.scrollHeight
    // Diagram placeholders may have (re)appeared with this update; cheap no-op otherwise.
    if (el) void renderMermaidBlocks(el)
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
  pendingTurn.value = null
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

function addFiles(files) {
  if (canAttach.value && files?.length) attachmentQueue.add(files)
}

function pasteFiles(event) {
  const files = event.clipboardData?.files
  if (files?.length) {
    event.preventDefault()
    addFiles(files)
  }
}

function dropFiles(event) {
  addFiles(event.dataTransfer?.files)
}

function submit() {
  const value = text.value.trim()
  if (!canSubmit.value || ws?.readyState !== WebSocket.OPEN) return
  const ids = readyAttachments.value.map(item => item.id)
  const clientTurnId = crypto.randomUUID()
  const payload = { type: 'chat', text: value, clientTurnId, ...(ids.length ? { attachments: ids } : {}) }
  try {
    ws.send(JSON.stringify(payload))
    pendingTurn.value = { id: clientTurnId, text: value }
    // Sending asks to see the reply, so re-pin even from a scrolled-up position.
    scrollToEnd({ force: true })
  } catch { /* retain the draft and attachments for retry */ }
}

function interrupt() {
  if (canSend.value && ws?.readyState === WebSocket.OPEN) ws.send(JSON.stringify({ type: 'interrupt' }))
}

function reconnectForCurrentSession() {
  closeSocket()
  void attachmentQueue.cancelAll()
  attachmentQueue = makeAttachmentQueue()
  attachmentVersion.value += 1
  log.reset()
  version.value += 1
  stickToBottom = true
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
  void attachmentQueue.cancelAll()
})
</script>
<template>
  <div class="pane">
    <div ref="scroller" class="chat-scroll" data-chat-scroll @scroll.passive="onScroll">
      <div class="chat-inner">
        <div v-if="!items.length && !drafts.length" class="empty">
          {{ isLive ? 'Send a message to start the conversation.' : 'No saved conversation.' }}
        </div>
        <template v-for="(item, i) in items" :key="i">
          <div v-if="item.kind === 'user'" class="bubble user" data-chat-user>
            <pre v-if="item.text">{{ item.text }}</pre>
            <ChatAttachments v-if="item.attachments?.length" :items="item.attachments" :retry="() => {}"
              :remove="() => {}" transcript />
          </div>
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
    <div v-if="canSend" class="composer" @dragover.prevent @drop.prevent="dropFiles">
      <ChatAttachments v-if="attachmentItems.length" :items="attachmentItems"
        :retry="attachmentQueue.retry" :remove="attachmentQueue.remove" />
      <div class="composer-row">
        <input v-if="canAttach" ref="fileInput" class="file-input" type="file" multiple
          accept=".png,.jpg,.jpeg,.webp,.gif,.pdf,.md,.markdown,.txt,.docx,.pptx,.xlsx"
          @change="addFiles($event.target.files); $event.target.value = ''">
        <button v-if="canAttach" type="button" class="attach" data-chat-attach aria-label="Attach files" title="Attach files"
          @click="fileInput?.click()">＋</button>
        <textarea v-model="text" data-chat-input rows="1" placeholder="Message the agent — paste, drop, or attach files"
          @paste="pasteFiles" @keydown.enter.exact.prevent="submit"></textarea>
        <button v-if="busy" class="stop" data-chat-stop @click="interrupt">◼ Stop</button>
        <button class="primary" data-chat-send :disabled="!canSubmit" @click="submit">Send</button>
      </div>
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
.composer { display: flex; flex-direction: column; gap: 9px; padding: 10px 16px 12px; background: var(--bg); border-top: 1px solid var(--border); }
.composer-row { display: flex; align-items: flex-end; gap: 10px; }
.composer textarea { flex: 1; background: var(--hover); border: 1px solid var(--border-2); border-radius: var(--radius); font-family: var(--ui); font-size: 14px; min-height: 42px; max-height: 180px; resize: vertical; }
.composer button { align-self: flex-end; padding: 9px 18px; white-space: nowrap; }
.composer .stop { color: var(--danger); border-color: var(--border-3); }
.composer .attach { width: 42px; height: 42px; padding: 0; color: var(--muted); border-color: var(--border-2); font-size: 20px; }
.composer .attach:hover { color: var(--accent); }
.file-input { position: absolute; width: 1px; height: 1px; overflow: hidden; opacity: 0; pointer-events: none; }
@media (max-width: 640px) { .composer { padding-inline: 10px; } .composer-row { gap: 6px; } .composer button { padding-inline: 12px; } }
</style>
