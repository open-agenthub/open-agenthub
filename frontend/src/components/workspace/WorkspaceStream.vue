<script setup>
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { api, getSharedTranscript, sharedTerminalUrl, terminalUrl } from '../../api.js'
import { createAttachmentQueue } from '../../lib/attachments.js'
import { createChatLog } from '../../lib/chat.js'
import { chatDrafts as sessionDrafts } from '../../lib/session-drafts.js'
import { renderMarkdown } from '../../lib/markdown.js'
import { renderMermaidBlocks } from '../../lib/mermaid.js'
import ChatAttachments from '../ChatAttachments.vue'
import WorkLog from './WorkLog.vue'
import MessageActions from './MessageActions.vue'

const props = defineProps({ session: Object, readonly: { type: Boolean, default: false }, sharedToken: { type: String, default: null } })
const emit = defineEmits(['status'])

const scroller = ref(null)
const text = ref(sessionDrafts.get(props.session.id) || '')
const fileInput = ref(null)
const composerInput = ref(null)
const connectionStatus = ref('connecting…')
const following = ref(true)
const deliveryError = ref('')
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
  connectionStatus.value === 'connected' && Boolean(text.value.trim() || readyAttachments.value.length))

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
      deliveryError.value = 'Message could not be delivered. Your draft and attachments are still here.'
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
  following.value = stickToBottom
}

let scrollQueued = false
function scrollToEnd({ force = false } = {}) {
  if (force) stickToBottom = true
  following.value = stickToBottom
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
  connectionStatus.value = 'disconnected'
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
    connectionStatus.value = 'connected'
  }
  socket.onmessage = event => {
    if (!disposed && ws === socket && typeof event.data === 'string') feed(event.data)
  }
  socket.onclose = () => {
    if (disposed || ws !== socket) return
    ws = undefined
    if (pendingTurn.value) deliveryError.value = 'Connection lost before delivery was confirmed. Check the conversation before retrying.'
    pendingTurn.value = null
    connectionStatus.value = 'disconnected'
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
  deliveryError.value = ''
  const ids = readyAttachments.value.map(item => item.id)
  try {
    // getRandomValues also works on local HTTP origins; randomUUID requires HTTPS.
    const clientTurnId = Array.from(crypto.getRandomValues(new Uint8Array(16)), byte => byte.toString(16).padStart(2, '0')).join('')
    const payload = { type: 'chat', text: value, clientTurnId, ...(ids.length ? { attachments: ids } : {}) }
    ws.send(JSON.stringify(payload))
    pendingTurn.value = { id: clientTurnId, text: value }
    // Sending asks to see the reply, so re-pin even from a scrolled-up position.
    scrollToEnd({ force: true })
  } catch { deliveryError.value = 'Could not send. Your draft and attachments are still here.' }
}

function interrupt() {
  if (canSend.value && ws?.readyState === WebSocket.OPEN) ws.send(JSON.stringify({ type: 'interrupt' }))
}

function quote(value) {
  text.value += `${text.value ? '\n\n' : ''}${value.split('\n').map(line => '> ' + line).join('\n')}\n\n`
  composerInput.value?.focus()
}

function composerKey(event) {
  if (event.key === 'Enter' && !event.shiftKey && !event.ctrlKey && !event.altKey && !event.metaKey && !event.isComposing) {
    event.preventDefault()
    submit()
  }
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

watch(() => props.session.id, (id, previous) => {
  sessionDrafts.set(previous, text.value)
  text.value = sessionDrafts.get(id) || ''
  deliveryError.value = ''
  reconnectForCurrentSession()
})
watch(isLive, (live, wasLive) => {
  if (live && !wasLive) connect()
  else if (!live && wasLive) closeSocket()
})

onBeforeUnmount(() => {
  sessionDrafts.set(props.session.id, text.value)
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
          <span class="workspace-symbol" aria-hidden="true">✳</span><h2>What shall we build?</h2>
          {{ isLive ? 'Send a message to start the conversation.' : 'No saved conversation.' }}
        </div>
        <template v-for="(item, i) in items" :key="i">
          <div v-if="item.kind === 'user'" class="bubble user" data-chat-user>
            <pre v-if="item.text">{{ item.text }}</pre>
            <ChatAttachments v-if="item.attachments?.length" :items="item.attachments" :retry="() => {}"
              :remove="() => {}" transcript />
            <MessageActions v-if="item.text" :text="item.text" :can-quote="canSend" @quote="quote" />
          </div>
          <div v-else-if="item.kind === 'assistant'" class="bubble assistant" data-chat-assistant>
            <template v-for="(block, j) in item.blocks" :key="j">
              <div v-if="block.type === 'text'" class="workspace-markdown" v-html="renderMarkdown(block.text)"></div>
              <details v-else-if="block.type === 'thinking'" class="fold thinking">
                <summary>Thinking</summary>
                <div class="workspace-markdown muted-md" v-html="renderMarkdown(block.text)"></div>
              </details>
              <WorkLog v-else-if="block.type === 'tool'" :label="block.name" :failed="block.isError"
                :state="block.isError ? 'failed' : block.result === null ? 'running…' : 'done'" :data-chat-tool="block.name">
                <pre>{{ pretty(block.input) }}</pre>
                <pre v-if="block.result">{{ clip(block.result) }}</pre>
              </WorkLog>
            </template>
            <MessageActions v-if="item.blocks.some(b => b.type === 'text')"
              :text="item.blocks.filter(b => b.type === 'text').map(b => b.text).join('\n\n')" :can-quote="canSend" @quote="quote" />
          </div>
          <div v-else-if="item.kind === 'stderr'" class="note err"><pre>{{ item.text }}</pre></div>
          <div v-else-if="item.kind === 'exit'" class="note">{{ item.text }}</div>
          <div v-else class="note">{{ item.text }}</div>
        </template>
        <div v-if="drafts.length" class="bubble assistant" data-chat-draft>
          <template v-for="draft in drafts" :key="draft.index">
            <details v-if="draft.type === 'thinking'" class="fold thinking" open>
              <summary>Thinking…</summary>
              <div class="workspace-markdown muted-md" v-html="renderMarkdown(draft.text)"></div>
            </details>
            <div v-else class="workspace-markdown" v-html="renderMarkdown(draft.text)"></div>
          </template>
        </div>
        <div v-else-if="busy" class="typing" data-chat-busy><span></span><span></span><span></span></div>
      </div>
    </div>
    <button v-if="!following" type="button" class="jump-latest" @click="scrollToEnd({ force: true })">↓ Jump to latest</button>
    <div v-if="canSend" class="composer" @dragover.prevent @drop.prevent="dropFiles">
      <ChatAttachments v-if="attachmentItems.length" :items="attachmentItems"
        :retry="attachmentQueue.retry" :remove="attachmentQueue.remove" />
      <div class="composer-row">
        <input v-if="canAttach" ref="fileInput" class="file-input" type="file" multiple
          accept=".png,.jpg,.jpeg,.webp,.gif,.pdf,.md,.markdown,.txt,.docx,.pptx,.xlsx"
          @change="addFiles($event.target.files); $event.target.value = ''">
        <button v-if="canAttach" type="button" class="attach" data-chat-attach aria-label="Attach files" title="Attach files"
          @click="fileInput?.click()">＋</button>
        <textarea ref="composerInput" v-model="text" data-chat-input rows="3" aria-label="Message the agent" placeholder="Message the agent — paste, drop, or attach files"
          @paste="pasteFiles" @keydown="composerKey"></textarea>
        <button v-if="busy" class="stop" data-chat-stop @click="interrupt">◼ Stop</button>
        <button class="primary" data-chat-send :disabled="!canSubmit" @click="submit">Send</button>
      </div>
      <div class="composer-footer"><span>{{ session.agent || 'Agent' }} · {{ connectionStatus }}</span><span>Enter to send · Shift+Enter for newline</span></div>
      <p v-if="deliveryError" class="delivery-error" role="alert">{{ deliveryError }}</p>
    </div>
  </div>
</template>
<style scoped>
.pane { position: relative; flex: 1; display: flex; flex-direction: column; min-width: 0; min-height: 0; background: var(--bg); }
.chat-scroll { flex: 1; overflow-y: auto; min-height: 0; }
.chat-inner { max-width: 840px; margin: auto; padding: 32px; display: flex; flex-direction: column; gap: 24px; }
.empty { color: var(--muted-3); font-size: 13px; padding: 24px 0; text-align: center; }
.bubble { font-size: 14px; line-height: 1.6; }
.bubble.user { align-self: flex-end; max-width: 85%; background: var(--panel-2); border: 1px solid var(--border-2); border-radius: 16px; padding: 14px 18px; }
.bubble.user pre { margin: 0; white-space: pre-wrap; word-break: break-word; font: 13px/1.6 var(--ui); color: var(--text); }
.bubble.assistant { align-self: stretch; color: var(--text); display: flex; flex-direction: column; gap: 8px; }
.muted-md { color: var(--muted-2); font-size: 13px; }
.fold { border: 1px solid var(--border); border-radius: var(--radius); background: var(--panel); }
.fold summary { display: flex; align-items: center; gap: 8px; padding: 7px 12px; font-size: 12px; font-weight: 600; color: var(--muted-2); cursor: pointer; user-select: none; }
.fold summary:hover { color: var(--text); }
.fold[open] summary { border-bottom: 1px solid var(--border); }
.fold > .workspace-markdown { padding: 10px 12px; }
.note { align-self: center; color: var(--muted-3); font-size: 12px; }
.note.err { align-self: stretch; color: var(--danger); }
.note.err pre { margin: 0; white-space: pre-wrap; word-break: break-word; font: 12px/1.5 var(--mono); }
.typing { display: flex; gap: 5px; padding: 4px 2px; }
.typing span { width: 7px; height: 7px; border-radius: 50%; background: var(--muted-3); animation: pulse 1.2s infinite ease-in-out; }
.typing span:nth-child(2) { animation-delay: 0.15s; }
.typing span:nth-child(3) { animation-delay: 0.3s; }
@keyframes pulse { 0%, 80%, 100% { opacity: 0.25; } 40% { opacity: 1; } }
.composer button { align-self: flex-end; padding: 9px 18px; white-space: nowrap; }
.composer .stop { color: var(--danger); border-color: var(--border-3); }
.composer .attach { width: 42px; height: 42px; padding: 0; color: var(--muted); border-color: var(--border-2); font-size: 20px; }
.composer .attach:hover { color: var(--accent); }
.file-input { position: absolute; width: 1px; height: 1px; overflow: hidden; opacity: 0; pointer-events: none; }
.composer { display: flex; flex-direction: column; flex-shrink: 0; gap: 9px; width: calc(100% - 64px); max-width: 840px; align-self: center; box-sizing: border-box; padding: 14px; margin: 8px 0 12px; border: 1px solid var(--border-2); border-radius: 18px; background: var(--panel); box-shadow: 0 6px 24px #0002; }
.composer:focus-within { border-color: var(--accent); }
.composer-row { display: flex; align-items: flex-end; gap: 10px; flex-wrap: wrap; }
.composer textarea { min-width: 0; min-height: 72px; max-height: 180px; resize: vertical; font: 14px/1.6 var(--ui); flex: 1 1 100%; order: -1; border: 0; padding: 0; background: transparent; box-shadow: none; }
.composer .attach { margin-right: auto; }
.composer-footer { display: flex; justify-content: space-between; flex-wrap: wrap; gap: 8px; color: var(--muted-3); font-size: 10px; }
.delivery-error { margin: 0; color: var(--danger); font-size: 12px; }
.workspace-symbol { display: block; margin: 40px 0 16px; font-size: 36px; color: var(--accent); }
.empty h2 { color: var(--strong); font-size: 24px; font-weight: 500; }
.jump-latest { position: absolute; left: 50%; bottom: 240px; transform: translateX(-50%); border-radius: 20px; font-size: 12px; padding: 7px 14px; }
@media(max-width: 640px) { .chat-inner { padding: 20px 16px; } .composer { width: calc(100% - 20px); } }
@media (max-width: 640px) { .composer { padding-inline: 10px; } .composer-row { gap: 6px; } .composer button { padding-inline: 12px; } }
</style>
