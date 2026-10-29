<script setup>
import { onBeforeUnmount, onMounted, ref } from 'vue'
import BrowserPane from './BrowserPane.vue'
const state = ref(null)
const opener = window.opener
function receive(event) {
  if (!opener || event.source !== opener || event.origin !== location.origin || event.data?.type !== 'browser-popout-state') return
  state.value = event.data
  document.title = `${event.data.session.title || 'Session'} · Browser`
}
function bringBack() {
  opener?.postMessage({ type: 'browser-popout-return' }, location.origin)
}
onMounted(() => {
  window.addEventListener('message', receive)
  opener?.postMessage({ type: 'browser-popout-ready' }, location.origin)
})
onBeforeUnmount(() => window.removeEventListener('message', receive))
</script>

<template>
  <main class="browser-popout">
    <button v-if="state" class="return-browser" type="button" @click="bringBack">Bring back to session</button>
    <BrowserPane v-if="state" :session="state.session" :can-write="state.canWrite" :shared-token="state.sharedToken" standalone />
    <p v-else>Open the browser from its session to use this window.</p>
  </main>
</template>

<style scoped>
.browser-popout { height: 100dvh; display: flex; flex-direction: column; }
.browser-popout :deep(.browser-pane) { flex: 1; }
.return-browser { align-self: flex-end; margin: 4px 12px; }
</style>
