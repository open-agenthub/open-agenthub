<script setup>
import { ref, onMounted, onBeforeUnmount, computed, watch } from 'vue'
import { Terminal } from '@xterm/xterm'
import { FitAddon } from '@xterm/addon-fit'
import { terminalUrl, shellUrl, api } from '../api.js'

// A single xterm instance wired to one WebSocket.
//   kind="agent" -> shared Claude terminal (/terminal); replays transcript when the
//                   session is finished (read-only).
//   kind="shell" -> interactive bash shell (/shell); always interactive, only used
//                   while the session is live.
const props = defineProps({
  session: Object,
  kind: { type: String, default: 'agent' },   // 'agent' | 'shell'
  active: { type: Boolean, default: true }     // whether this pane's tab is visible
})
const emit = defineEmits(['status'])

const host = ref(null)
const status = ref('connecting…')
const mobileInput = ref('')
let term, fit, ws, ro, fitTimer

function setStatus(s) { status.value = s; emit('status', s) }

// Debounced, no-op-avoiding fit (see original TerminalView): only refit when the
// computed grid actually changes, and coalesce bursts.
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

// The Claude terminal is read-only once the session finished; a shell pane is only
// mounted while the session is live, so it is always interactive.
const isLive = computed(() =>
  props.kind === 'shell' ||
  props.session?.phase === 'Running' || props.session?.phase === 'Pending')

function send(obj) { if (ws?.readyState === WebSocket.OPEN) ws.send(JSON.stringify(obj)) }
function sendResize() { if (term) send({ type: 'resize', cols: term.cols, rows: term.rows }) }
function sendMobileLine() { send({ type: 'input', data: mobileInput.value + '\r' }); mobileInput.value = '' }

function urlFor() {
  return props.kind === 'shell' ? shellUrl(props.session.id) : terminalUrl(props.session.id)
}

async function connect() {
  ws = new WebSocket(await urlFor())
  ws.onopen = () => { setStatus('connected'); sendResize() }
  ws.onmessage = (ev) => term.write(typeof ev.data === 'string' ? ev.data : new Uint8Array(ev.data))
  ws.onclose = () => { setStatus('disconnected'); if (isLive.value) setTimeout(() => term && connect(), 2000) }
  ws.onerror = () => { setStatus('error') }
}

async function loadTranscript() {
  setStatus('history')
  const text = await api.getTranscript(props.session.id)
  term.write(text || '\r\n[no saved transcript]\r\n')
}

// When a hidden tab becomes visible again, its container regained size — refit.
watch(() => props.active, (v) => { if (v && term) { scheduleFit(sendResize); setTimeout(() => term?.focus(), 60) } })

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
  <div class="pane">
    <div ref="host" class="term"></div>
    <!-- Mobile reply box only makes sense for the agent conversation. -->
    <div v-if="isLive && kind === 'agent'" class="mobile-input">
      <input v-model="mobileInput" placeholder="Type a reply and send…"
             @keyup.enter="sendMobileLine" autocapitalize="off" autocomplete="off" spellcheck="false" />
      <button class="primary" @click="sendMobileLine">Send</button>
    </div>
  </div>
</template>

<style scoped>
.pane { flex: 1; display: flex; flex-direction: column; min-width: 0; min-height: 0; background: #0E1116; }
.term { flex: 1; padding: 8px 10px; min-height: 0; }
.mobile-input { display: none; gap: 8px; padding: 10px; border-top: 1px solid var(--border); background: var(--panel); padding-bottom: max(10px, env(safe-area-inset-bottom)); }
.mobile-input input { flex: 1; }
@media (max-width: 760px) { .mobile-input { display: flex; } }
</style>
