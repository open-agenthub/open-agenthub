<script setup>
// A running container keeps the environment it started with; files in ~/.secrets follow a change
// within about a minute, variables do not (#255, docs/session-secrets.md). The hub reports that
// difference as `secretsPendingRestart`, and this says so where the change was made, with the
// restart that applies it. Two clicks, because a restart interrupts a turn in progress.
import { ref } from 'vue'
import { api } from '../api.js'
import { canApplyNow } from '../lib/resources.js'

const props = defineProps({
  session: { type: Object, required: true },
  // Shown in lists of several sessions (Settings → Secrets) so each note names its session.
  named: { type: Boolean, default: false }
})
const emit = defineEmits(['restarted'])

const armed = ref(false)
const busy = ref(false)
const error = ref('')

async function restart() {
  if (!armed.value) { armed.value = true; return }
  busy.value = true
  error.value = ''
  try {
    // apply-resources restarts for changed variables too, with the same checks: interactive,
    // running, state saved first so the conversation continues.
    emit('restarted', await api.applySessionResources(props.session.id))
  } catch (e) {
    error.value = String(e.message || e)
  } finally {
    busy.value = false
    armed.value = false
  }
}
</script>

<template>
  <div v-if="session.secretsPendingRestart" class="secret-restart" role="status" :data-secret-restart-note="session.id">
    <span><template v-if="named">“{{ session.title || session.id }}”: </template>changed secret variables reach the agent
      at its next start; the running process still has the previous ones. Files in <code>~/.secrets</code> are already current.</span>
    <button v-if="canApplyNow(session)" type="button" :class="{ armed }" :disabled="busy" data-secret-restart
      @click="restart">{{ busy ? 'Restarting…' : armed ? 'Really restart? A turn in progress is interrupted' : 'Restart now' }}</button>
    <span v-else class="later">It is not running interactively, so it is not restarted for them.</span>
    <small v-if="error" class="err" role="alert">{{ error }}</small>
  </div>
</template>

<style scoped>
.secret-restart { display: flex; flex-wrap: wrap; align-items: center; gap: 6px 10px; margin: 8px 0; padding: 8px 10px; border: 1px solid var(--warn); border-radius: 8px; font-size: 12px; color: var(--text); line-height: 1.5; }
.secret-restart span { flex: 1 1 240px; min-width: 0; }
code { font-family: var(--mono); font-size: 11px; }
button { width: auto; padding: 3px 10px; font-size: 12px; }
button.armed { border-color: var(--warn); color: var(--warn); }
.later { color: var(--muted-3); }
.err { flex-basis: 100%; color: var(--danger); font-family: var(--mono); }
</style>
