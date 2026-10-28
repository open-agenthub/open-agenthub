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
const resyncing = ref(false)
const resyncError = ref('')

// #303: copies withdrawn after a failed rewrite. Retrying writes them again; it restarts nothing.
async function resync() {
  resyncing.value = true
  resyncError.value = ''
  try { emit('restarted', await api.resyncSessionSecrets(props.session.id)) }
  catch (e) { resyncError.value = String(e.message || e) }
  finally { resyncing.value = false }
}
const busy = ref(false)
const error = ref('')

async function restart() {
  // A schedule's template update restarts nothing, so it needs no second click.
  if (!armed.value && !props.session.scheduleSecretsOutdated) { armed.value = true; return }
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
  <template v-if="session.secretsWithdrawn || session.scheduleSecretsOutdated || session.secretsPendingRestart">
  <div v-if="session.secretsWithdrawn" class="secret-restart" role="status" :data-secrets-withdrawn-note="session.id">
    <span><template v-if="named">“{{ session.title || session.id }}”: </template>this session's secrets are withdrawn
      after an update could not be written, so no stale value stays behind. Its files in <code>~/.secrets</code>
      <template v-if="session.mode === 'Scheduled'">and its next runs</template> have none until they are written again,
      which is retried at the next secret or project change.</span>
    <button type="button" :disabled="resyncing" data-secrets-resync @click="resync">{{ resyncing ? 'Retrying…' : 'Retry' }}</button>
    <small v-if="resyncError" class="err" role="alert">{{ resyncError }}</small>
  </div>
  <div v-if="session.scheduleSecretsOutdated" class="secret-restart" role="status" :data-schedule-secrets-note="session.id">
    <span><template v-if="named">“{{ session.title || session.id }}”: </template>the next scheduled run would still start with
      the previous secret variables, because its schedule could not be updated. It is retried at the next secret or project change.</span>
    <button type="button" :disabled="busy" data-schedule-secrets-update @click="restart">{{ busy ? 'Updating…' : 'Update schedule' }}</button>
    <small v-if="error" class="err" role="alert">{{ error }}</small>
  </div>
  <div v-else-if="session.secretsPendingRestart" class="secret-restart" role="status" :data-secret-restart-note="session.id">
    <span><template v-if="named">“{{ session.title || session.id }}”: </template>changed secret variables reach the agent
      at its next start; the running process still has the previous ones<template v-if="session.secretsWithdrawn">, including
      any secret deleted since it started</template>.<template v-if="!session.secretsWithdrawn"> Files in <code>~/.secrets</code> are already current.</template></span>
    <button v-if="canApplyNow(session)" type="button" :class="{ armed }" :disabled="busy" data-secret-restart
      @click="restart">{{ busy ? 'Restarting…' : armed ? 'Really restart? A turn in progress is interrupted' : 'Restart now' }}</button>
    <span v-else class="later">It is not running interactively, so it is not restarted for them.</span>
    <small v-if="error" class="err" role="alert">{{ error }}</small>
  </div>
  </template>
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
