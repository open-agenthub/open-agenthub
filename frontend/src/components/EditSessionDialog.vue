<script setup>
import { ref } from 'vue'
import { api } from '../api.js'
import RepoPicker from './RepoPicker.vue'

const props = defineProps({ session: Object })
const emit = defineEmits(['close', 'updated'])

const f = ref({
  title: props.session.title,
  image: props.session.image || '',
  runAsRoot: !!props.session.runAsRoot,
  cpu: props.session.cpu || '500m',
  memory: props.session.memory || '1Gi',
  mcpConfigJson: props.session.mcpConfigJson || ''
})
const repos = ref((props.session.repos || []).map(r => ({ ...r })))
const busy = ref(false)
const error = ref('')

const scheduled = props.session.mode === 'Scheduled'

async function save() {
  busy.value = true; error.value = ''
  if (f.value.mcpConfigJson.trim()) {
    try { JSON.parse(f.value.mcpConfigJson) }
    catch { error.value = 'MCP config is not valid JSON.'; busy.value = false; return }
  }
  try {
    const payload = scheduled
      ? { title: f.value.title }
      : {
          title: f.value.title,
          image: f.value.image.trim(),          // empty = default agent image
          runAsRoot: f.value.runAsRoot,
          cpu: f.value.cpu.trim(),
          memory: f.value.memory.trim(),
          repos: repos.value,
          mcpConfigJson: f.value.mcpConfigJson   // "" clears it
        }
    const updated = await api.updateSession(props.session.id, payload)
    emit('updated', updated)
  } catch (e) { error.value = String(e.message || e) }
  finally { busy.value = false }
}
</script>

<template>
  <div class="overlay" @click.self="$emit('close')">
    <div class="modal">
      <h3>Edit session</h3>
      <p class="note" v-if="scheduled">Scheduled sessions run from a fixed CronJob spec — only the title can be changed here.</p>
      <p class="note" v-else>The title applies immediately. Image, root mode and resources take effect the next time the session is resumed.</p>

      <div class="field">
        <label>Title</label>
        <input v-model="f.title" />
      </div>
      <template v-if="!scheduled">
        <div class="field">
          <label>Repositories</label>
          <RepoPicker v-model="repos" />
        </div>
        <div class="field">
          <label>MCP config (.mcp.json, empty = none)</label>
          <textarea v-model="f.mcpConfigJson" placeholder='{ "mcpServers": { … } }'></textarea>
        </div>
        <div class="field">
          <label>Container image (empty = default agent image)</label>
          <input v-model="f.image" placeholder="ghcr.io/…/my-image:tag" />
        </div>
        <div class="grid">
          <div class="field"><label>CPU (request)</label><input v-model="f.cpu" placeholder="500m" /></div>
          <div class="field"><label>Memory (request)</label><input v-model="f.memory" placeholder="1Gi" /></div>
        </div>
        <label class="check">
          <input type="checkbox" v-model="f.runAsRoot" /> Run as root (install tools via apt, npm -g, …)
        </label>
      </template>

      <p v-if="error" class="err">{{ error }}</p>
      <div class="row">
        <button @click="$emit('close')">Cancel</button>
        <button class="primary" :disabled="busy" @click="save">{{ busy ? 'Saving…' : 'Save' }}</button>
      </div>
    </div>
  </div>
</template>

<style scoped>
.overlay {
  position: fixed; inset: 0; background: rgba(5,7,10,.7);
  display: flex; align-items: flex-start; justify-content: center; padding: 24px; overflow-y: auto; z-index: 50;
}
.modal { width: 480px; max-width: 100%; background: var(--panel); border: 1px solid var(--border); border-radius: 14px; padding: 22px; }
.modal h3 { margin: 0 0 6px; }
.note { color: var(--muted); font-size: 12px; line-height: 1.5; margin: 0 0 16px; }
.grid { display: grid; grid-template-columns: 1fr 1fr; gap: 12px; }
.check { display: flex; align-items: center; gap: 8px; font-size: 13px; color: var(--text); margin: 4px 0 12px; }
.check input { width: auto; }
.row { display: flex; justify-content: flex-end; gap: 10px; margin-top: 8px; }
.err { color: var(--danger); font-family: var(--mono); font-size: 12px; }
@media (max-width: 760px) { .grid { grid-template-columns: 1fr; } }
</style>
