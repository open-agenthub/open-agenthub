<script setup>
// What the agent of this session asked for with `secret_request` (docs/session-secrets.md). The
// value is never typed here: "Add it" opens the pre-filled form in Settings → Secrets, the same
// link the chat relays post, so a request is answered in one place whichever way it was noticed.
import { onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { api } from '../api.js'

const props = defineProps({ sessionId: { type: String, required: true } })
const requests = ref([])
const busy = ref('')
let timer

async function refresh() {
  try { requests.value = (await api.listSecretRequests(props.sessionId)) || [] } catch { /* older backend */ }
}

async function refuse(request) {
  busy.value = request.id
  try { await api.refuseSecretRequest(request.id); await refresh() }
  finally { busy.value = '' }
}

const kind = delivery => (delivery === 'file' ? 'file in ~/.secrets' : 'environment variable')
const link = request => `/settings/secrets?request=${encodeURIComponent(request.id)}`

onMounted(() => {
  refresh()
  // The same cadence as the permission prompts next to it: a request is answered while the agent
  // waits, so a minute-long poll would leave it waiting for nothing.
  timer = setInterval(refresh, 5000)
})
onBeforeUnmount(() => clearInterval(timer))
watch(() => props.sessionId, refresh)
</script>

<template>
  <div v-for="request in requests" :key="request.id" class="secret-request" :data-secret-request="request.id">
    <div class="text">
      <b>The agent asks for a secret:</b> <code>{{ request.name }}</code>
      <span class="dim">({{ kind(request.delivery) }})</span>
      <span v-if="request.purpose" class="purpose">— {{ request.purpose }}</span>
    </div>
    <div class="actions">
      <a class="button primary" :href="link(request)" data-secret-request-add>Add it…</a>
      <button type="button" :disabled="busy === request.id" data-secret-request-refuse @click="refuse(request)">Refuse</button>
    </div>
  </div>
</template>

<style scoped>
.secret-request { display: flex; align-items: center; gap: 12px; flex-wrap: wrap; margin: 8px 12px; padding: 10px 12px; border: 1px solid var(--warn); border-radius: 10px; background: var(--panel); font-size: 13px; }
.text { flex: 1; min-width: 0; overflow-wrap: anywhere; color: var(--text); }
code { font-family: var(--mono); font-size: 12px; }
.dim { margin-left: 4px; color: var(--faint); }
.purpose { color: var(--muted-2); }
.actions { display: flex; gap: 8px; }
.button { display: inline-flex; align-items: center; padding: 5px 12px; border-radius: 8px; font-size: 12px; font-weight: 600; text-decoration: none; }
.button.primary { background: var(--accent); color: #0b0b0b; }
.actions button { padding: 5px 12px; font-size: 12px; }
</style>
