<script setup>
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { browserUrl, resizeBrowserViewport, sharedBrowserUrl } from '../api.js'

const props = defineProps({
  session: { type: Object, required: true },
  canWrite: { type: Boolean, default: false },
  sharedToken: { type: String, default: null }
})
const host = ref(null)
const connectionState = ref('waiting')
const MIN_VIEWPORT = { width: 480, height: 320 }
const MAX_VIEWPORT = { width: 2560, height: 1600 }
const RESIZE_DELAY = 250
let rfb
let reconnectTimer
let reconnectAttempt = 0
let generation = 0
let disposed = false
let resizeObserver
let resizeTimer
let latestViewport
let lastSubmittedViewport
let inFlightViewport

const phase = computed(() => props.session?.browser?.phase || 'Stopped')
const statusLabel = computed(() => {
  if (phase.value === 'Pending') return 'Preparing browser…'
  if (phase.value === 'Stopping') return 'Browser is stopping…'
  if (phase.value === 'Failed') return 'Browser could not be displayed.'
  if (connectionState.value === 'connected') return props.canWrite ? 'Live · shared control' : 'Live · view only'
  if (connectionState.value === 'reconnecting') return 'Reconnecting…'
  if (connectionState.value === 'failed') return 'Browser connection unavailable.'
  return 'Connecting browser…'
})

function viewportKey(viewport) {
  return `${viewport.sessionId}:${viewport.width}x${viewport.height}`
}
function validViewport(viewport) {
  return viewport.width >= MIN_VIEWPORT.width &&
    viewport.height >= MIN_VIEWPORT.height &&
    viewport.width <= MAX_VIEWPORT.width &&
    viewport.height <= MAX_VIEWPORT.height
}
function mayResizeViewport() {
  return !disposed && phase.value === 'Running' && props.canWrite && !props.sharedToken
}
function clearViewportTimer() {
  if (resizeTimer) clearTimeout(resizeTimer)
  resizeTimer = undefined
}
function queueViewportResize() {
  const element = host.value
  if (!element) return
  latestViewport = {
    sessionId: props.session.id,
    width: Math.round(element.clientWidth),
    height: Math.round(element.clientHeight)
  }
  clearViewportTimer()
  if (!mayResizeViewport() || !validViewport(latestViewport)) return
  const key = viewportKey(latestViewport)
  if (key === lastSubmittedViewport || key === inFlightViewport) return

  resizeTimer = setTimeout(async () => {
    resizeTimer = undefined
    const viewport = latestViewport
    if (!viewport || !mayResizeViewport() || !validViewport(viewport) ||
        viewport.sessionId !== props.session.id) return
    const currentKey = viewportKey(viewport)
    if (currentKey === lastSubmittedViewport || currentKey === inFlightViewport) return
    inFlightViewport = currentKey
    try {
      await resizeBrowserViewport(viewport.sessionId, viewport.width, viewport.height)
      lastSubmittedViewport = currentKey
    } catch {
      // A later ResizeObserver event retries without disturbing the VNC session.
    } finally {
      if (inFlightViewport === currentKey) inFlightViewport = undefined
    }
  }, RESIZE_DELAY)
}

function clearReconnect() {
  if (reconnectTimer) clearTimeout(reconnectTimer)
  reconnectTimer = undefined
}
function closeRfb() {
  generation += 1
  clearReconnect()
  const current = rfb
  rfb = undefined
  current?.disconnect()
}
function scheduleReconnect(currentGeneration) {
  if (disposed || phase.value !== 'Running' || currentGeneration !== generation) return
  connectionState.value = 'reconnecting'
  const delay = Math.min(10_000, 750 * (2 ** reconnectAttempt++))
  reconnectTimer = setTimeout(connect, delay)
}
async function connect() {
  if (disposed || phase.value !== 'Running' || !host.value) return
  clearReconnect()
  const currentGeneration = ++generation
  const sessionId = props.session.id
  try {
    const url = props.sharedToken ? sharedBrowserUrl(props.sharedToken) : await browserUrl(sessionId)
    if (disposed || currentGeneration !== generation || sessionId !== props.session.id) return
    const { default: RFB } = await import('@novnc/novnc')
    if (disposed || currentGeneration !== generation || sessionId !== props.session.id) return
    const connection = new RFB(host.value, url)
    rfb = connection
    connection.scaleViewport = true
    connection.resizeSession = false
    connection.viewOnly = !props.canWrite
    connection.addEventListener('connect', () => {
      if (rfb !== connection) return
      reconnectAttempt = 0
      connectionState.value = 'connected'
      queueViewportResize()
    })
    connection.addEventListener('disconnect', event => {
      if (rfb !== connection) return
      rfb = undefined
      if (event?.detail?.clean) connectionState.value = 'waiting'
      else scheduleReconnect(currentGeneration)
    })
    connection.addEventListener('securityfailure', () => {
      if (rfb === connection) connectionState.value = 'failed'
    })
  } catch {
    scheduleReconnect(currentGeneration)
  }
}
function syncConnection() {
  clearViewportTimer()
  latestViewport = undefined
  lastSubmittedViewport = undefined
  inFlightViewport = undefined
  closeRfb()
  reconnectAttempt = 0
  connectionState.value = 'waiting'
  if (phase.value === 'Running') void connect()
}

onMounted(() => {
  resizeObserver = new ResizeObserver(queueViewportResize)
  resizeObserver.observe(host.value)
  syncConnection()
})
watch([
  () => props.session.id,
  phase,
  () => props.sharedToken
], syncConnection)
watch(() => props.canWrite, canWrite => {
  if (rfb) rfb.viewOnly = !canWrite
  if (canWrite) queueViewportResize()
  else clearViewportTimer()
})
onBeforeUnmount(() => {
  disposed = true
  clearViewportTimer()
  resizeObserver?.disconnect()
  closeRfb()
})
</script>

<template>
  <section data-browser-pane class="browser-pane" :class="`is-${phase.toLowerCase()}`" :aria-label="statusLabel">
    <div class="browser-strip">
      <span class="browser-mark"><i></i>Browser</span>
      <span class="browser-state">{{ statusLabel }}</span>
    </div>
    <div ref="host" class="browser-canvas"></div>
    <div v-if="phase !== 'Running' || connectionState !== 'connected'" class="browser-overlay" aria-live="polite">
      <span class="orbit" aria-hidden="true"></span>
      <strong>{{ statusLabel }}</strong>
      <small v-if="phase === 'Pending'">The agent requested a browser. It appears here automatically.</small>
    </div>
  </section>
</template>

<style scoped>
.browser-pane { position: relative; display: flex; flex-direction: column; min-width: 0; min-height: 0; height: 100%; overflow: hidden; background: #111721; }
.browser-strip { height: 34px; flex: 0 0 34px; display: flex; align-items: center; justify-content: space-between; gap: 12px; padding: 0 12px; color: #aeb8c8; background: #171c24; border-bottom: 1px solid #29303a; font: 10px var(--mono); letter-spacing: .035em; }
.browser-mark { display: inline-flex; align-items: center; gap: 7px; color: #eef5ff; font-weight: 700; text-transform: uppercase; }
.browser-mark i { width: 7px; height: 7px; border-radius: 2px; background: var(--accent); box-shadow: 0 0 0 3px rgba(90,169,245,.12); }
.browser-state { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.browser-canvas { flex: 1; min-height: 0; overflow: hidden; background: #0b0f15; }
.browser-canvas :deep(canvas) { outline: none; }
.browser-overlay { position: absolute; inset: 34px 0 0; display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 8px; padding: 24px; text-align: center; color: #dce5f1; background: radial-gradient(circle at 50% 42%, rgba(90,169,245,.09), transparent 34%), #10151c; }
.browser-overlay strong { font: 600 13px var(--ui); }
.browser-overlay small { max-width: 330px; color: #7f8a9a; font: 11px/1.55 var(--ui); }
.orbit { width: 23px; height: 23px; border: 1px solid #3b4655; border-top-color: var(--accent); border-radius: 50%; animation: orbit .9s linear infinite; }
.is-failed .orbit { border-radius: 6px; border-color: var(--danger); animation: none; transform: rotate(45deg); }
@keyframes orbit { to { transform: rotate(360deg); } }
</style>
