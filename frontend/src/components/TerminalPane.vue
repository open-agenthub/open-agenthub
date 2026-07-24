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
let fitFrame = 0
let lastSentCols = 0
let lastSentRows = 0

const isLive = computed(() => props.kind === 'shell' || ['Running', 'Pending'].includes(props.session?.phase))
const canSend = computed(() => isLive.value && !props.readonly)

function send(value) {
  if (canSend.value && ws?.readyState === WebSocket.OPEN) ws.send(JSON.stringify(value))
}

function resize(force = false) {
  if (!term) return
  if (!force && term.cols === lastSentCols && term.rows === lastSentRows) return
  lastSentCols = term.cols
  lastSentRows = term.rows
  send({ type: 'resize', cols: term.cols, rows: term.rows })
}

function fitNow() {
  if (disposed || !term || !fit || !host.value) return
  // Hidden panes (v-show) report a zero-size host; fitting there collapses the grid.
  if (!host.value.clientWidth || !host.value.clientHeight) return
  const dims = fit.proposeDimensions?.()
  if (!dims || !Number.isFinite(dims.cols) || !Number.isFinite(dims.rows)) return
  const cols = Math.max(2, dims.cols)
  const rows = Math.max(1, dims.rows)
  if (cols !== term.cols || rows !== term.rows) {
    term.resize(cols, rows)
    resize()
  }
}

function scheduleFit() {
  if (fitFrame) return
  fitFrame = requestAnimationFrame(() => {
    fitFrame = 0
    fitNow()
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
    resize(true)
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
  term = new Terminal({ fontFamily: "'JetBrains Mono', monospace", fontSize: 13, cursorBlink: canSend.value, disableStdin: !canSend.value, theme: { background: '#0e0d0b', foreground: '#c9c4bb' } })
  fit = new FitAddon()
  term.loadAddon(fit)
  term.open(host.value)
  fitNow()
  ro = new ResizeObserver(scheduleFit)
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
  if (visible && term) scheduleFit()
})

onBeforeUnmount(() => {
  disposed = true
  closeSocket()
  if (fitFrame) {
    cancelAnimationFrame(fitFrame)
    fitFrame = 0
  }
  ro?.disconnect()
  ro = undefined
  term?.dispose()
  term = undefined
  fit = undefined
})
</script>
<template><div class="pane"><div ref="host" class="term"></div><div v-if="canSend && kind === 'agent'" class="composer"><input v-model="mobileInput" placeholder="Message the agent…" @keyup.enter="send({ type: 'input', data: mobileInput + '\r' }); mobileInput = ''" /><button class="primary" @click="send({ type: 'input', data: mobileInput + '\r' }); mobileInput = ''">Send</button></div></div></template>
<style scoped>.pane { flex: 1; display: flex; flex-direction: column; min-width: 0; min-height: 0; background: #0e0d0b; } .term { flex: 1; min-height: 0; padding: 10px 12px; overflow: hidden; } .composer { display: flex; gap: 10px; padding: 12px 16px; background: var(--bg); border-top: 1px solid var(--border); } .composer input { flex: 1; background: var(--hover); border: 1px solid var(--border-2); border-radius: var(--radius); } .composer button { align-self: center; padding: 9px 18px; }</style>
