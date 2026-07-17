<script setup>
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { Terminal } from '@xterm/xterm'
import { FitAddon } from '@xterm/addon-fit'
import { api, getSharedTranscript, sharedTerminalUrl, shellUrl, terminalUrl } from '../api.js'

const props = defineProps({ session: Object, kind: { type: String, default: 'agent' }, active: { type: Boolean, default: true }, readonly: { type: Boolean, default: false }, sharedToken: { type: String, default: null } })
const emit = defineEmits(['status'])
const host = ref(null)
const mobileInput = ref('')
let term, fit, ws, ro, reconnectTimer
let disposed = false
let inputRegistered = false
let connectionGeneration = 0

const isLive = computed(() => props.kind === 'shell' || ['Running', 'Pending'].includes(props.session?.phase))
const canSend = computed(() => isLive.value && !props.readonly)

function send(value) {
  if (canSend.value && ws?.readyState === WebSocket.OPEN) ws.send(JSON.stringify(value))
}

function resize() {
  if (term) send({ type: 'resize', cols: term.cols, rows: term.rows })
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

function enableInput() {
  if (!inputRegistered && canSend.value && term) {
    inputRegistered = true
    term.onData(data => send({ type: 'input', data }))
  }
}

async function connect() {
  if (disposed || !isLive.value) return
  clearReconnect()
  const generation = ++connectionGeneration
  const sessionId = props.session.id
  const url = props.sharedToken
    ? sharedTerminalUrl(props.sharedToken)
    : await (props.kind === 'shell' ? shellUrl(sessionId) : terminalUrl(sessionId))
  if (disposed || generation !== connectionGeneration || sessionId !== props.session.id) return

  const socket = new WebSocket(url)
  ws = socket
  socket.onopen = () => {
    if (disposed || ws !== socket) return
    emit('status', 'connected')
    resize()
  }
  socket.onmessage = event => {
    if (!disposed && ws === socket && term) term.write(typeof event.data === 'string' ? event.data : new Uint8Array(event.data))
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
  const text = props.sharedToken ? await getSharedTranscript(props.sharedToken) : await api.getTranscript(sessionId)
  if (!disposed && sessionId === props.session.id && term) term.write(text || '\r\n[no saved transcript]\r\n')
}

function reconnectForCurrentSession() {
  closeSocket()
  term?.clear?.()
  if (isLive.value) {
    enableInput()
    connect()
  } else {
    transcript()
  }
}

onMounted(() => {
  term = new Terminal({ fontFamily: "'JetBrains Mono', monospace", fontSize: 13, cursorBlink: canSend.value, disableStdin: !canSend.value, theme: { background: '#0E1116', foreground: '#D7DCE3' } })
  fit = new FitAddon()
  term.loadAddon(fit)
  term.open(host.value)
  fit.fit()
  ro = new ResizeObserver(() => {
    fit?.fit()
    resize()
  })
  ro.observe(host.value)
  if (isLive.value) {
    enableInput()
    connect()
  } else {
    transcript()
  }
})

watch(() => props.session.id, reconnectForCurrentSession)
watch(isLive, (live, wasLive) => {
  if (term) {
    term.options.disableStdin = !canSend.value
    term.options.cursorBlink = canSend.value
  }
  if (live && !wasLive) {
    enableInput()
    connect()
  } else if (!live && wasLive) {
    closeSocket()
  }
})
watch(() => props.readonly, () => {
  if (term) term.options.disableStdin = !canSend.value
  enableInput()
})
watch(() => props.active, visible => {
  if (visible && term) {
    fit.fit()
    resize()
  }
})

onBeforeUnmount(() => {
  disposed = true
  closeSocket()
  ro?.disconnect()
  ro = undefined
  term?.dispose()
  term = undefined
  fit = undefined
})
</script>
<template><div class="pane"><div ref="host" class="term"></div><div v-if="canSend && kind === 'agent'" class="mobile-input"><input v-model="mobileInput" placeholder="Type a reply and send…" @keyup.enter="send({ type: 'input', data: mobileInput + '\r' }); mobileInput = ''" /><button class="primary" @click="send({ type: 'input', data: mobileInput + '\r' }); mobileInput = ''">Send</button></div></div></template>
<style scoped>.pane { flex: 1; display: flex; flex-direction: column; min-width: 0; min-height: 0; background: #0E1116; } .term { flex: 1; min-height: 0; padding: 8px 10px; } .mobile-input { display: none; gap: 8px; padding: 10px; background: var(--panel); } .mobile-input input { flex: 1; } @media (max-width: 760px) { .mobile-input { display: flex; } }</style>
