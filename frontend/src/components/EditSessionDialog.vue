<script setup>
import { computed, ref, watch } from 'vue'
import { api } from '../api.js'
import RepoPicker from './RepoPicker.vue'

const props = defineProps({ session: Object, projects: Array, embedded: { type: Boolean, default: false } })
const emit = defineEmits(['close', 'updated'])

const f = ref({})
const repos = ref([])
const advOpen = ref(false)
const busy = ref(false)
const error = ref('')

const scheduled = computed(() => props.session.mode === 'Scheduled')

function reset(session) {
  f.value = {
    title: session.title,
    image: session.image || '',
    runAsRoot: !!session.runAsRoot,
    cpu: session.cpu || '500m',
    memory: session.memory || '1Gi',
    mcpConfigJson: session.mcpConfigJson || '',
    projectId: session.projectId || '',
  }
  repos.value = (session.repos || []).map(repo => ({ ...repo }))
  busy.value = false
  error.value = ''
}

reset(props.session)
watch(() => props.session.id, () => reset(props.session))

async function save() {
  busy.value = true; error.value = ''
  if (f.value.mcpConfigJson.trim()) {
    try { JSON.parse(f.value.mcpConfigJson) }
    catch { error.value = 'MCP config is not valid JSON.'; busy.value = false; return }
  }
  try {
    const payload = scheduled.value
      ? { title: f.value.title, projectId: f.value.projectId || null }
      : {
          title: f.value.title,
          image: f.value.image.trim(),          // empty = default agent image
          runAsRoot: f.value.runAsRoot,
          cpu: f.value.cpu.trim(),
          memory: f.value.memory.trim(),
          repos: repos.value,
          mcpConfigJson: f.value.mcpConfigJson,  // "" clears it
          projectId: f.value.projectId || null
        }
    const updated = await api.updateSession(props.session.id, payload)
    emit('updated', updated)
  } catch (e) { error.value = String(e.message || e) }
  finally { busy.value = false }
}
</script>

<template>
  <div :class="embedded ? 'embed' : 'overlay'" @click.self="embedded || $emit('close')">
    <div :class="embedded ? 'embed-inner' : 'modal'">
      <h3 class="form-title">Edit session</h3>
      <p class="note" v-if="scheduled">Scheduled sessions run from a fixed CronJob spec — only title and project can be changed here.</p>
      <p class="note" v-else>The title applies immediately. Image, root mode and resources take effect the next time the session is resumed.</p>

      <div class="card sect">
        <div class="field">
          <label>Title</label>
          <input v-model="f.title" />
        </div>
        <div class="field last"><label>Project</label><select v-model="f.projectId"><option value="">No project</option><option v-for="project in projects" :key="project.id" :value="project.id">{{ project.name }}</option></select></div>
      </div>
      <template v-if="!scheduled">
        <div class="card sect">
          <label>Repositories</label>
          <RepoPicker v-model="repos" />
        </div>
        <div class="card adv">
          <button class="adv-head" @click="advOpen = !advOpen">
            <span><b>Advanced</b><span class="dim adv-sub">MCP, container, resources</span></span>
            <span class="dim">{{ advOpen ? '▾' : '▸' }}</span>
          </button>
          <div v-if="advOpen" class="adv-body">
            <div class="field">
              <label>Extra tools <span class="dim">— MCP servers (.mcp.json), empty = none</span></label>
              <textarea v-model="f.mcpConfigJson" placeholder='{ "mcpServers": { … } }'></textarea>
            </div>
            <div class="grid3">
              <div class="field"><label>Container image</label><input v-model="f.image" class="mono" placeholder="default agent image" /></div>
              <div class="field"><label>CPU</label><input v-model="f.cpu" class="mono" placeholder="500m" /></div>
              <div class="field"><label>Memory</label><input v-model="f.memory" class="mono" placeholder="1Gi" /></div>
            </div>
            <label class="check">
              <input type="checkbox" v-model="f.runAsRoot" />
              <span><b>Run as root</b> — install tools via apt, npm&nbsp;-g, …</span>
            </label>
          </div>
        </div>
      </template>

      <p v-if="error" class="err">{{ error }}</p>
      <div class="row">
        <button class="primary" :disabled="busy" @click="save">{{ busy ? 'Saving…' : 'Save changes' }}</button>
        <button @click="$emit('close')">Cancel</button>
        <span class="dim note-inline">Changes apply on next agent turn — the session keeps running.</span>
      </div>
    </div>
  </div>
</template>

<style scoped>
.overlay { position: fixed; inset: 0; background: rgba(10,9,8,.7); display: flex; align-items: flex-start; justify-content: center; padding: 24px; overflow-y: auto; z-index: 50; }
.modal { width: 620px; max-width: 100%; background: var(--panel); border: 1px solid var(--border-2); border-radius: var(--radius-lg); padding: 22px; }
.embed-inner { max-width: 720px; }
.form-title { font-size: 20px; margin: 0 0 6px; }
.note { color: var(--muted-2); font-size: 12px; line-height: 1.5; margin: 0 0 16px; }
.sect { padding: 18px 20px; margin-bottom: 16px; }
.field.last { margin-bottom: 0; }
.dim { color: var(--faint); font-weight: 400; }
.mono { font-family: var(--mono); font-size: 13px; }
.adv { overflow: hidden; margin-bottom: 16px; }
.adv-head { width: 100%; display: flex; align-items: center; justify-content: space-between; padding: 14px 20px; border: none; background: none; border-radius: 0; font-size: 13px; color: var(--text); }
.adv-head:hover { background: var(--hover); }
.adv-sub { margin-left: 10px; font-size: 12px; }
.adv-body { padding: 14px 20px 20px; border-top: 1px solid var(--border); }
.grid3 { display: grid; grid-template-columns: 2fr 1fr 1fr; gap: 14px; }
.check { display: flex; align-items: flex-start; gap: 10px; margin: 4px 0 0; font-size: 13px; color: var(--text); cursor: pointer; }
.check input { width: auto; margin-top: 2px; }
.row { display: flex; align-items: center; gap: 10px; padding-bottom: 8px; }
.note-inline { font-size: 12px; }
.err { color: var(--danger); font-family: var(--mono); font-size: 12px; }
@media (max-width: 760px) { .grid3 { grid-template-columns: 1fr; } }
</style>
