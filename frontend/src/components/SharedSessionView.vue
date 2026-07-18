<script setup>
import { onMounted, ref } from 'vue'
import { getSharedSession } from '../api.js'
import TerminalView from './TerminalView.vue'

const props = defineProps({ token: { type: String, required: true } })
const session = ref(null)
const error = ref('')

onMounted(async () => {
  try { session.value = await getSharedSession(props.token) }
  catch (e) { error.value = String(e.message || e) }
})
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
