<script setup>
import { ref, watch } from 'vue'
import { api } from '../api.js'

const props = defineProps({ session: Object, projects: Array, embedded: { type: Boolean, default: false } })
const emit = defineEmits(['close', 'duplicated'])
const title = ref('')
const projectId = ref('')
const includeMcp = ref(true)
const busy = ref(false); const error = ref('')
function reset(session) {
  title.value = `Copy of ${session.title}`
  projectId.value = session.projectId || ''
  includeMcp.value = true
  busy.value = false
  error.value = ''
}
reset(props.session)
watch(() => props.session.id, () => reset(props.session))
async function submit() {
  busy.value = true; error.value = ''
  try { emit('duplicated', await api.duplicateSession(props.session.id, { title: title.value.trim() || `Copy of ${props.session.title}`, projectId: projectId.value || null, includeMcp: includeMcp.value })) }
  catch (e) { error.value = String(e.message || e) } finally { busy.value = false }
}
</script>
<template>
  <div :class="embedded ? 'embed' : 'overlay'" @click.self="embedded || $emit('close')"><div :class="embedded ? 'embed-inner' : 'modal'">
    <h3 class="form-title">Duplicate session</h3>
    <p class="note">Copies the configuration of “{{ session.title }}” into an independent new session — without conversation history, artifacts, or sharing settings.</p>
    <div class="card sect">
      <div class="field"><label>Title</label><input v-model="title" /></div>
      <div class="field"><label>Project</label><select v-model="projectId"><option value="">No project</option><option v-for="project in projects" :key="project.id" :value="project.id">{{ project.name }}</option></select></div>
      <label class="check"><input v-model="includeMcp" type="checkbox" /> <span>Include MCP configuration</span></label>
    </div>
    <p v-if="error" class="err">{{ error }}</p>
    <div class="row"><button class="primary" :disabled="busy" @click="submit">{{ busy ? 'Duplicating…' : 'Duplicate' }}</button><button @click="$emit('close')">Cancel</button></div>
  </div></div>
</template>
<style scoped>
.overlay { position: fixed; inset: 0; z-index: 50; display: flex; align-items: flex-start; justify-content: center; overflow-y: auto; padding: 24px; background: rgba(10,9,8,.7); }
.modal { width: 520px; max-width: 100%; padding: 22px; border: 1px solid var(--border-2); border-radius: var(--radius-lg); background: var(--panel); }
.embed-inner { max-width: 680px; }
.form-title { font-size: 20px; margin: 0 0 6px; }
.note { margin: 0 0 16px; color: var(--muted-2); font-size: 12px; line-height: 1.5; }
.sect { padding: 18px 20px; margin-bottom: 16px; }
.check { display: flex; gap: 10px; align-items: center; font-size: 13px; color: var(--text); cursor: pointer; margin: 0; }
.check input { width: auto; }
.row { display: flex; gap: 10px; }
.err { color: var(--danger); font: 12px var(--mono); }
</style>
