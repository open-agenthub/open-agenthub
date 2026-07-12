<script setup>
import { ref, onMounted } from 'vue'
import { api } from '../api.js'

const emit = defineEmits(['close'])
const providers = ref([])
const error = ref('')
const loading = ref(true)

async function load() {
  loading.value = true
  try { providers.value = await api.gitProviders() }
  catch (e) { error.value = String(e.message || e) }
  finally { loading.value = false }
}

async function connect(p) {
  try {
    const { url } = await api.gitConnectUrl(p.id)
    // Full-page navigation to the provider; it redirects back to /account.
    location.href = url
  } catch (e) { error.value = String(e.message || e) }
}

async function disconnect(p) {
  if (!confirm(`Disconnect ${p.displayName}?`)) return
  try { await api.gitDisconnect(p.id); await load() }
  catch (e) { error.value = String(e.message || e) }
}

onMounted(() => {
  load()
  // Clean up the ?git=connected|error marker left by the OAuth callback redirect.
  const q = new URLSearchParams(location.search)
  if (q.has('git')) { error.value = q.get('git') === 'error' ? 'Connecting the account failed.' : ''; history.replaceState(null, '', '/account') }
})
</script>

<template>
  <div class="overlay" @click.self="$emit('close')">
    <div class="modal">
      <h3>Account · Git connections</h3>
      <p class="note">Connect a GitHub or GitLab account so sessions can list, clone and push your projects — no personal access token needed. Tokens are stored server-side and never shown.</p>

      <p v-if="loading" class="muted">Loading…</p>
      <p v-else-if="!providers.length" class="muted">No Git providers are configured on this instance.</p>

      <div v-for="p in providers" :key="p.id" class="prov">
        <div class="info">
          <div class="name">{{ p.displayName }} <span class="type">{{ p.type }}</span></div>
          <div class="status" :class="{ on: p.connected }">
            {{ p.connected ? (p.username ? `connected as ${p.username}` : 'connected') : 'not connected' }}
          </div>
        </div>
        <button v-if="p.connected" class="danger" @click="disconnect(p)">Disconnect</button>
        <button v-else class="primary" @click="connect(p)">Connect</button>
      </div>

      <p v-if="error" class="err">{{ error }}</p>
      <div class="row"><button @click="$emit('close')">Close</button></div>
    </div>
  </div>
</template>

<style scoped>
.overlay { position: fixed; inset: 0; background: rgba(5,7,10,.7); display: flex; align-items: flex-start; justify-content: center; padding: 24px; overflow-y: auto; z-index: 50; }
.modal { width: 480px; max-width: 100%; background: var(--panel); border: 1px solid var(--border); border-radius: 14px; padding: 22px; }
.modal h3 { margin: 0 0 6px; }
.note { color: var(--muted); font-size: 12px; line-height: 1.5; margin: 0 0 16px; }
.muted { color: var(--muted); font-size: 13px; }
.prov { display: flex; align-items: center; gap: 12px; padding: 12px 0; border-top: 1px solid var(--border); }
.info { flex: 1; min-width: 0; }
.name { font-weight: 600; font-size: 14px; }
.type { font-family: var(--mono); font-size: 10px; color: var(--muted); border: 1px solid var(--border); border-radius: 999px; padding: 1px 7px; margin-left: 6px; }
.status { font-size: 12px; color: var(--muted); margin-top: 2px; }
.status.on { color: var(--ok); }
.row { display: flex; justify-content: flex-end; gap: 10px; margin-top: 16px; }
.err { color: var(--danger); font-family: var(--mono); font-size: 12px; }
</style>
