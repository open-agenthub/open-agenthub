<script setup>
// Which stored secrets a project's sessions receive (docs/session-secrets.md). A session in the
// project gets this selection unless its own dialog narrows it; narrowing the project here takes a
// secret away from its running sessions as well.
import { computed, onMounted, ref } from 'vue'
import { api } from '../api.js'
import { deliveryLabel, secretConflicts } from '../lib/secrets.js'
import SecretConflictNote from './SecretConflictNote.vue'

const props = defineProps({ project: { type: Object, required: true } })
const emit = defineEmits(['close', 'saved'])

const secrets = ref([])
const selected = ref([...(props.project.secretIds || [])])
const loaded = ref(false)
const busy = ref(false)
const error = ref('')
// A project selecting two secrets that land in the same place would hand both to every session in
// it; the hub refuses that, and the dialog says so before Save (#253).
const ticked = computed(() => secrets.value.filter(s => selected.value.includes(s.id)))
const clash = computed(() => secretConflicts(ticked.value).length > 0)

onMounted(async () => {
  try { secrets.value = (await api.listSecrets()) || [] }
  catch (e) { error.value = String(e.message || e) }
  finally { loaded.value = true }
})

function toggle(id) {
  selected.value = selected.value.includes(id) ? selected.value.filter(item => item !== id) : [...selected.value, id]
}

async function save() {
  busy.value = true
  error.value = ''
  try {
    // Only ids that are still stored: one removed since the project was loaded would be a 400.
    const ids = secrets.value.map(s => s.id).filter(id => selected.value.includes(id))
    emit('saved', await api.updateProject(props.project.id, { secretIds: ids }))
  } catch (e) {
    error.value = String(e.message || e)
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <div class="overlay" data-project-secrets @click.self="$emit('close')">
    <div class="modal">
      <h3>Secrets for “{{ project.name }}”</h3>
      <p class="note">Sessions in this project receive what is ticked, unless a session narrows it. Files reach
        running sessions within about a minute, variables at their next start.</p>
      <p v-if="loaded && !secrets.length && !error" class="note" data-project-secrets-empty>No secrets stored yet —
        add them under Settings → Secrets.</p>
      <label v-for="secret in secrets" :key="secret.id" class="check">
        <input type="checkbox" :checked="selected.includes(secret.id)" :data-project-secret="secret.id" @change="toggle(secret.id)" />
        <span><code>{{ secret.name }}</code> <span class="kind">{{ deliveryLabel(secret.delivery) }}</span><span v-if="secret.description" class="dim desc">{{ secret.description }}</span></span>
      </label>
      <SecretConflictNote :secrets="ticked" subject="Every session in this project" />
      <p v-if="error" class="err" data-project-secrets-error>{{ error }}</p>
      <div class="row">
        <button class="primary" data-project-secrets-save :disabled="busy || !loaded || clash" @click="save">{{ busy ? 'Saving…' : 'Save' }}</button>
        <button @click="$emit('close')">Cancel</button>
      </div>
    </div>
  </div>
</template>

<style scoped>
.overlay { position: fixed; inset: 0; z-index: 50; display: flex; align-items: flex-start; justify-content: center; overflow-y: auto; padding: 24px; background: rgba(10,9,8,.7); }
.modal { width: 480px; max-width: 100%; padding: 22px; border: 1px solid var(--border-2); border-radius: var(--radius-lg); background: var(--panel); }
h3 { margin: 0 0 6px; font-size: 18px; }
.note { margin: 0 0 14px; color: var(--muted-2); font-size: 12px; line-height: 1.5; }
.check { display: flex; align-items: center; gap: 10px; margin: 6px 0; font-size: 13px; color: var(--text); cursor: pointer; }
.check input { width: auto; }
code { font-family: var(--mono); font-size: 12px; }
.kind { display: inline-block; margin-left: 6px; padding: 1px 6px; border: 1px solid var(--border-2); border-radius: 4px; color: var(--muted-3); font-size: 11px; font-weight: 700; }
.dim { color: var(--faint); font-weight: 400; }
.desc { margin-left: 6px; }
.err { color: var(--danger); font: 12px var(--mono); }
.row { display: flex; gap: 10px; margin-top: 16px; }
</style>
