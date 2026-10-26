<script setup>
import { computed, onMounted, ref } from 'vue'
import { api } from '../api.js'
const props = defineProps({ session: Object, projects: Array, target: String, sessions: Array })
const emit = defineEmits(['close', 'changed', 'reorder'])
const dialog = ref(null)
const target = ref(props.target ?? props.session.projectId ?? '')
const busy = ref(false)
const error = ref('')
const current = computed(() => props.projects.find(p => p.id === props.session.projectId))
const destination = computed(() => props.projects.find(p => p.id === target.value))
const changed = computed(() => (props.session.projectId || '') !== target.value)
const active = computed(() => ['Running', 'Pending'].includes(props.session.phase))
const scheduled = computed(() => props.session.mode === 'Scheduled')
const removed = computed(() => (current.value?.secretIds || []).filter(id => !destination.value?.secretIds?.includes(id)).length)
const added = computed(() => (destination.value?.secretIds || []).filter(id => !current.value?.secretIds?.includes(id)).length)
const siblings = computed(() => props.sessions.filter(s => (!s.accessRole || s.accessRole === 'Owner') && (s.projectId || '') === (props.session.projectId || '')))
const index = computed(() => siblings.value.findIndex(s => s.id === props.session.id))
onMounted(() => dialog.value.showModal())
async function move() {
  busy.value = true; error.value = ''
  try {
    if (active.value) {
      await api.pauseSession(props.session.id)
      emit('changed')
    }
    await api.moveSessionProject(props.session.id, target.value || null)
    emit('changed'); emit('close')
  } catch (e) { error.value = String(e.message || e) }
  finally { busy.value = false }
}
</script>
<template>
  <dialog ref="dialog" aria-labelledby="organize-title" @cancel.prevent="busy || emit('close')">
    <h3 id="organize-title">Organize {{ session.title }}</h3>
    <div class="row">
      <button :disabled="busy || index <= 0" @click="emit('reorder', session.id, siblings[index - 1]?.id)">Move session up</button>
      <button :disabled="busy || index < 0 || index >= siblings.length - 1" @click="emit('reorder', session.id, siblings[index + 2]?.id || null)">Move session down</button>
    </div>
    <label for="move-project">Move to project</label>
    <select id="move-project" v-model="target" :disabled="busy">
      <option value="">No project</option>
      <option v-for="p in projects" :key="p.id" :value="p.id">{{ p.name }}</option>
    </select>
    <div v-if="changed" data-project-move-impact>
      <p>Move from <b>{{ current?.name || 'No project' }}</b> to <b>{{ destination?.name || 'No project' }}</b>.</p>
      <p>Project secrets: {{ removed }} removed, {{ added }} added to the project selection. A narrowed session selection stays narrowed; individual grants stay with this session.</p>
      <p>Access to files in the old project ends; files in the destination project become available, subject to sharing rules. This session’s own files stay with it.</p>
      <p v-if="active">The session will pause before moving. Resume it afterwards to load the destination project’s secrets and settings. Work may be interrupted.</p>
      <p v-else>Destination project secrets and settings apply on the next start. Project file access changes immediately.</p>
      <p v-if="scheduled" role="status">Scheduled sessions use a fixed workload template. Recreate the scheduled session in the destination project.</p>
    </div>
    <p v-if="error" role="alert">{{ error }} If the session was paused, it stays paused; you can retry or resume it.</p>
    <div class="row">
      <button class="primary" data-confirm-move :disabled="busy || !changed || scheduled" @click="move">{{ busy ? 'Moving…' : active ? 'Pause and move' : 'Confirm move' }}</button>
      <button :disabled="busy" @click="emit('close')">Close</button>
    </div>
  </dialog>
</template>
<style scoped>
dialog { width: min(520px, calc(100vw - 32px)); padding: 24px; color: var(--text); background: var(--panel); border: 1px solid var(--border-2); border-radius: var(--radius-lg); }
dialog::backdrop { background: rgba(10,9,8,.7); }
h3 { margin-top: 0; }
label { display: block; margin-top: 18px; }
.row { display: flex; gap: 8px; margin-top: 16px; }
p { line-height: 1.5; }
[role="alert"] { color: var(--danger); }
</style>
