<script setup>
import { onMounted, onUnmounted, ref } from 'vue'
import { getSharedSession } from '../api.js'
import TerminalView from './TerminalView.vue'

const props = defineProps({ token: { type: String, required: true } })
const session = ref(null)
const error = ref('')

let refreshTimer
let refreshing = false
let active = false
async function refresh() {
  if (refreshing || !active) return
  refreshing = true
  try {
    const next = await getSharedSession(props.token)
    if (active) { session.value = next; error.value = '' }
  } catch (e) {
    if (active) { session.value = null; error.value = String(e.message || e) }
  } finally { refreshing = false }
}
onMounted(async () => {
  active = true
  await refresh()
  if (active) refreshTimer = setInterval(refresh, 2000)
})
onUnmounted(() => { active = false; clearInterval(refreshTimer) })
</script>

<template>
  <main class="shared-view">
    <header class="shared-head">
      <div><img src="/favicon.svg" alt="" class="logo" /><strong>Open AgentHub</strong><span>Shared session</span></div>
      <span v-if="session" class="shared-meta">{{ session.accessRole }}<template v-if="session.sharedBy"> · shared by {{ session.sharedBy }}</template></span>
    </header>
    <TerminalView v-if="session" :session="session" :shared-token="token" />
    <div v-else class="shared-state" :class="{ error: error }">{{ error || 'Loading shared session…' }}</div>
  </main>
</template>

<style scoped>
.shared-view { display: flex; flex-direction: column; height: 100%; background: var(--bg); }
.shared-head { display: flex; align-items: center; justify-content: space-between; gap: 16px; padding: 12px 18px; border-bottom: 1px solid var(--border); background: var(--sidebar); }
.shared-head div { display: flex; align-items: center; gap: 10px; }
.shared-head .logo { width: 24px; height: 24px; border-radius: 8px; }
.shared-head strong { font-family: var(--display); font-size: 15px; color: var(--strong); }
.shared-head div span, .shared-meta { color: var(--muted-3); font: 12px var(--mono); }
.shared-state { margin: auto; color: var(--muted); }
.shared-state.error { color: var(--danger); }
</style>
