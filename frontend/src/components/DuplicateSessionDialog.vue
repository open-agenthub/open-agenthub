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
  <div :class="embedded ? 'embed' : 'overlay'" @click.self="embedded || $emit('close')"><div :class="embedded ? 'embed-inner' : 'modal'"><h3>Duplicate session</h3><p class="note">Creates an independent session without conversation history, artifacts, or sharing settings.</p>
    <div class="field"><label>Title</label><input v-model="title" /></div><div class="field"><label>Project</label><select v-model="projectId"><option value="">Ungrouped</option><option v-for="project in projects" :key="project.id" :value="project.id">{{ project.name }}</option></select></div>
    <label class="check"><input v-model="includeMcp" type="checkbox" /> Include MCP configuration</label><p v-if="error" class="err">{{ error }}</p><div class="row"><button @click="$emit('close')">Cancel</button><button class="primary" :disabled="busy" @click="submit">{{ busy ? 'Duplicating…' : 'Duplicate' }}</button></div>
  </div></div>
</template>
<style scoped>
.overlay { position: fixed; inset: 0; z-index: 50; display: flex; align-items: flex-start; justify-content: center; overflow-y: auto; padding: 24px; background: rgba(5,7,10,.7); } .modal { width: 480px; max-width: 100%; padding: 22px; border: 1px solid var(--border); border-radius: 14px; background: var(--panel); } h3 { margin: 0 0 6px; } .note { margin: 0 0 16px; color: var(--muted); font-size: 12px; line-height: 1.5; } .check { display: flex; gap: 8px; align-items: center; } .check input { width: auto; } .row { display: flex; justify-content: flex-end; gap: 10px; margin-top: 16px; } .err { color: var(--danger); font: 12px var(--mono); }
</style>
