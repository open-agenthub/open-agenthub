<script setup>
import { computed, onBeforeUnmount, ref } from 'vue'
import BrowserPane from './BrowserPane.vue'

const props = defineProps({
  session: { type: Object, required: true },
  canWrite: { type: Boolean, default: false },
  sharedToken: { type: String, default: null }
})
const splitPercent = ref(50)
const mobilePane = ref('browser')
const browserVisible = computed(() => (props.session?.browser?.phase || 'Stopped') !== 'Stopped')
let dragging = false

function clamp(value) { return Math.min(75, Math.max(25, value)) }
function resizeBy(delta) { splitPercent.value = clamp(splitPercent.value + delta) }
function onSeparatorKey(event) {
  if (event.key === 'ArrowLeft') { event.preventDefault(); resizeBy(-5) }
  if (event.key === 'ArrowRight') { event.preventDefault(); resizeBy(5) }
  if (event.key === 'Home') { event.preventDefault(); splitPercent.value = 25 }
  if (event.key === 'End') { event.preventDefault(); splitPercent.value = 75 }
}
function onPointerMove(event) {
  if (!dragging) return
  const workspace = event.currentTarget?.document?.querySelector?.('[data-session-split]') || document.querySelector('[data-session-split]')
  const bounds = workspace?.getBoundingClientRect()
  if (bounds?.width) splitPercent.value = clamp(((event.clientX - bounds.left) / bounds.width) * 100)
}
function stopDragging() {
  dragging = false
  window.removeEventListener('pointermove', onPointerMove)
  window.removeEventListener('pointerup', stopDragging)
}
function startDragging(event) {
  event.preventDefault()
  dragging = true
  window.addEventListener('pointermove', onPointerMove)
  window.addEventListener('pointerup', stopDragging)
}
onBeforeUnmount(stopDragging)
</script>

<template>
  <div v-if="browserVisible" class="workspace-shell">
    <nav class="workspace-tabs" aria-label="Session view">
      <button :class="{ active: mobilePane === 'browser' }" @click="mobilePane = 'browser'">Browser</button>
      <button :class="{ active: mobilePane === 'terminal' }" @click="mobilePane = 'terminal'">Agent</button>
    </nav>
    <div data-session-split class="session-split" :style="{ '--browser-width': `${splitPercent}%` }">
      <div class="browser-side" :class="{ 'mobile-hidden': mobilePane !== 'browser' }">
        <BrowserPane :session="session" :can-write="canWrite" :shared-token="sharedToken" />
      </div>
      <div class="split-seam" role="separator" tabindex="0" aria-label="Browser width" aria-orientation="vertical" aria-valuemin="25" aria-valuemax="75" :aria-valuenow="Math.round(splitPercent)" @keydown="onSeparatorKey" @pointerdown="startDragging"><span></span></div>
      <div class="terminal-side" :class="{ 'mobile-hidden': mobilePane !== 'terminal' }"><slot /></div>
    </div>
  </div>
  <div v-else class="terminal-only"><slot /></div>
</template>

<style scoped>
.workspace-shell, .terminal-only { flex: 1; display: flex; flex-direction: column; min-width: 0; min-height: 0; }
.session-split { flex: 1; display: flex; min-width: 0; min-height: 0; }
.browser-side { flex: 0 0 var(--browser-width); min-width: 0; min-height: 0; }
.terminal-side { flex: 1 1 auto; display: flex; min-width: 0; min-height: 0; }
.split-seam { position: relative; flex: 0 0 9px; cursor: col-resize; background: #151719; border-inline: 1px solid #292a29; outline: none; touch-action: none; }
.split-seam span { position: absolute; inset: 50% 2px auto; height: 42px; transform: translateY(-50%); border-radius: 6px; background: #353a40; transition: background .15s, box-shadow .15s; }
.split-seam:hover span, .split-seam:focus-visible span { background: var(--accent); box-shadow: 0 0 0 3px rgba(90,169,245,.13); }
.workspace-tabs { display: none; height: 38px; flex: 0 0 38px; align-items: center; gap: 2px; padding: 4px; background: var(--bg); border-bottom: 1px solid var(--border); }
.workspace-tabs button { flex: 1; padding: 5px 10px; border: 0; border-radius: 7px; color: var(--muted-3); background: transparent; font-size: 12px; }
.workspace-tabs button.active { color: var(--strong); background: var(--panel-2); }
@media (max-width: 760px) {
  .workspace-tabs { display: flex; }
  .session-split { display: block; }
  .browser-side, .terminal-side { width: 100%; height: 100%; }
  .split-seam { display: none; }
  .mobile-hidden { display: none; }
}
</style>
