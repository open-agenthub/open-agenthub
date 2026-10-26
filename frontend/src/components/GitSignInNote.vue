<script setup>
// A connected provider that refused to renew its stored sign-in (revoked, or the app removed) gives
// the session no token for that host any more, and git then fails with an authentication error
// that names nothing (#289, docs/git-credentials.md). This says which provider and offers the
// sign-in; the hub hands the new token to the running session without a restart.
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { api } from '../api.js'

const props = defineProps({ session: { type: Object, required: true } })

// A refusal can happen while the session runs; once a minute is far below the renewal cadence.
const RECHECK_MS = 60_000

const providers = ref([])
const error = ref('')
let timer = null

const used = computed(() => new Set((props.session.repos || []).map(r => r.providerId).filter(Boolean)))
const refused = computed(() => providers.value.filter(p => p.needsSignIn && used.value.has(p.id)))

async function load() {
  if (!used.value.size) { providers.value = []; return }
  try { providers.value = await api.gitProviders() } catch { /* the note stays as it was */ }
}

async function signIn(p) {
  error.value = ''
  try {
    const { url } = await api.gitConnectUrl(p.id)
    location.href = url
  } catch (e) { error.value = String(e.message || e) }
}

onMounted(() => { load(); timer = setInterval(load, RECHECK_MS) })
onBeforeUnmount(() => clearInterval(timer))
watch(() => props.session.id, load)
</script>

<template>
  <div v-for="p in refused" :key="p.id" class="git-sign-in" role="alert" :data-git-sign-in="p.id">
    <span>{{ p.displayName }} no longer accepts your stored sign-in, so this session has no {{ p.displayName }}
      token: git and the CLI fail against it until you sign in again. The running session picks up the new
      sign-in without a restart.</span>
    <button type="button" class="primary" data-git-sign-in-button @click="signIn(p)">Sign in to {{ p.displayName }} again</button>
    <small v-if="error" class="err">{{ error }}</small>
  </div>
</template>

<style scoped>
.git-sign-in { display: flex; flex-wrap: wrap; align-items: center; gap: 6px 10px; margin: 8px 0; padding: 8px 10px; border: 1px solid var(--danger); border-radius: 8px; font-size: 12px; color: var(--text); line-height: 1.5; }
.git-sign-in span { flex: 1 1 240px; min-width: 0; }
button { width: auto; padding: 3px 10px; font-size: 12px; }
.err { flex-basis: 100%; color: var(--danger); font-family: var(--mono); }
</style>
