<script setup>
import { ref, onMounted, onBeforeUnmount, computed } from 'vue'
import { Terminal } from '@xterm/xterm'
import { FitAddon } from '@xterm/addon-fit'
import { terminalUrl, api } from '../api.js'

const props = defineProps({ session: Object })
const emit = defineEmits(['back', 'resume'])

const host = ref(null)
const status = ref('connecting…')
const mobileInput = ref('')
let term, fit, ws, ro, fitTimer

// Debounced, no-op-avoiding fit: continuous resizes (window drag, Google Meet
// tab-share re-layout) fired fit() on every frame, which flickered. Only refit
// when the computed grid actually changes, and coalesce bursts.
function scheduleFit(after) {
  clearTimeout(fitTimer)
  fitTimer = setTimeout(() => {
    if (!term || !fit) return
    try {
      const dims = fit.proposeDimensions?.()
      if (!dims || !Number.isFinite(dims.cols) || !Number.isFinite(dims.rows)) return
      if (dims.cols === term.cols && dims.rows === term.rows) return
      fit.fit()
      after?.()
    } catch {}
  }, 120)
}

const isLive = computed(() => props.session?.phase === 'Running' || props.session?.phase === 'Pending')

function send(obj) { if (ws?.readyState === WebSocket.OPEN) ws.send(JSON.stringify(obj)) }
function sendResize() { if (term) send({ type: 'resize', cols: term.cols, rows: term.rows }) }
function sendMobileLine() { send({ type: 'input', data: mobileInput.value + '\r' }); mobileInput.value = '' }

async function connect() {
  // terminalUrl fetches a fresh access token first (?access_token=... for the WS handshake).
  ws = new WebSocket(await terminalUrl(props.session.id))
  ws.onopen = () => { status.value = 'connected'; sendResize() }
  ws.onmessage = (ev) => term.write(typeof ev.data === 'string' ? ev.data : new Uint8Array(ev.data))
  ws.onclose = () => { status.value = 'disconnected'; if (isLive.value) setTimeout(() => term && connect(), 2000) }
  ws.onerror = () => { status.value = 'error' }
}

async function loadTranscript() {
  status.value = 'history'
  const text = await api.getTranscript(props.session.id)
  term.write(text || '\r\n[no saved transcript]\r\n')
}

onMounted(() => {
  term = new Terminal({
    fontFamily: "'JetBrains Mono', monospace", fontSize: 13,
    cursorBlink: isLive.value, scrollback: 8000, disableStdin: !isLive.value,
    theme: { background: '#0E1116', foreground: '#D7DCE3', cursor: '#5AA9F5', selectionBackground: '#2A313C' }
  })
  fit = new FitAddon(); term.loadAddon(fit); term.open(host.value); fit.fit()

  if (isLive.value) {
    term.onData(d => send({ type: 'input', data: d }))
    ro = new ResizeObserver(() => scheduleFit(sendResize))
    ro.observe(host.value)
    connect()
  } else {
    ro = new ResizeObserver(() => scheduleFit())
    ro.observe(host.value)
    loadTranscript()
  }
})

onBeforeUnmount(() => { clearTimeout(fitTimer); ro?.disconnect(); ws?.close(); term?.dispose(); term = null })
</script>

<template>
  <div class="term-wrap">
    <div class="term-bar">
      <button class="back" @click="$emit('back')">‹ Back</button>
      <span class="title">{{ session.title }}</span>
      <span class="right">
        <button v-if="session.canResume" class="resume" @click="$emit('resume', session.id)">↻ Resume</button>
        <span class="status" :class="status">{{ status }}</span>
      </span>
    </div>
    <div ref="host" class="term"></div>
    <div v-if="isLive" class="mobile-input">
      <input v-model="mobileInput" placeholder="Type a reply and send…"
             @keyup.enter="sendMobileLine" autocapitalize="off" autocomplete="off" spellcheck="false" />
      <button class="primary" @click="sendMobileLine">Send</button>
    </div>
  </div>
</template>

<style scoped>
.term-wrap { flex: 1; display: flex; flex-direction: column; min-width: 0; background: #0E1116; }
.term-bar { display: flex; align-items: center; gap: 12px; padding: 8px 14px; border-bottom: 1px solid var(--border); background: var(--panel); }
.term-bar .title { flex: 1; font-size: 13px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.term-bar .right { display: flex; align-items: center; gap: 10px; }
.back { background: none; border: none; color: var(--muted); }
.back:hover { color: var(--accent); border: none; }
.resume { font-size: 12px; padding: 5px 10px; }
.resume:hover { border-color: var(--running); color: var(--running); }
.status { font-family: var(--mono); font-size: 12px; color: var(--muted); }
.status.connected { color: var(--running); }
.status.disconnected, .status.error { color: var(--danger); }
.term { flex: 1; padding: 8px 10px; min-height: 0; }
.mobile-input { display: none; gap: 8px; padding: 10px; border-top: 1px solid var(--border); background: var(--panel); padding-bottom: max(10px, env(safe-area-inset-bottom)); }
.mobile-input input { flex: 1; }
@media (max-width: 760px) { .mobile-input { display: flex; } }
</style>
