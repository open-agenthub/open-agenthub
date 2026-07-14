<script setup>
import { ref, computed } from 'vue'
import { api } from '../api.js'
import RepoPicker from './RepoPicker.vue'

const emit = defineEmits(['close', 'created'])
const props = defineProps({ embedded: { type: Boolean, default: false } })

const repos = ref([])
const form = ref({
  title: '',
  mode: 'Interactive',
  prompt: '',
  schedule: '0 6 * * 1-5',
  mcpConfigJson: '',
  allowedToolsRaw: 'Edit,Bash(git*),Read',
  image: '',
  runAsRoot: false,
  cpu: '500m',
  memory: '1Gi'
})
const busy = ref(false)
const error = ref('')

const needsPrompt = computed(() => form.value.mode !== 'Interactive')
const needsSchedule = computed(() => form.value.mode === 'Scheduled')

async function submit() {
  error.value = ''
  if (form.value.mcpConfigJson.trim()) {
    try { JSON.parse(form.value.mcpConfigJson) }
    catch { error.value = 'MCP config is not valid JSON.'; return }
  }
  busy.value = true
  try {
    const session = await api.createSession({
      title: form.value.title || 'Session',
      mode: form.value.mode,
      repos: repos.value,
      prompt: form.value.prompt || null,
      schedule: needsSchedule.value ? form.value.schedule : null,
      mcpConfigJson: form.value.mcpConfigJson || null,
      allowedTools: form.value.allowedToolsRaw.split(',').map(s => s.trim()).filter(Boolean),
      image: form.value.image.trim() || null,
      runAsRoot: form.value.runAsRoot,
      cpu: form.value.cpu,
      memory: form.value.memory
    })
    emit('created', session)
  } catch (e) {
    error.value = String(e.message || e)
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <div :class="embedded ? 'embed' : 'overlay'" @click.self="embedded || $emit('close')">
    <div :class="embedded ? 'embed-inner' : 'modal'">
      <h3>New Session</h3>

      <div class="field">
        <label>Title</label>
        <input v-model="form.title" placeholder="e.g. Resolve ticket OPS-1423" />
      </div>

      <div class="field">
        <label>Mode</label>
        <select v-model="form.mode">
          <option value="Interactive">Interactive – watch & reply</option>
          <option value="Autonomous">Autonomous – works through a prompt on its own</option>
          <option value="Scheduled">Scheduled – runs on a schedule (CronJob)</option>
        </select>
      </div>

      <div class="field">
        <label>Repositories (optional — pick one or more)</label>
        <RepoPicker v-model="repos" />
      </div>

      <div class="field" v-if="needsPrompt">
        <label>Task / prompt</label>
        <textarea v-model="form.prompt" placeholder="Describe the task for the agent…"></textarea>
      </div>

      <div class="field" v-if="needsSchedule">
        <label>Schedule (cron)</label>
        <input v-model="form.schedule" placeholder="0 6 * * 1-5" />
      </div>

      <div class="field" v-if="needsPrompt">
        <label>Allowed tools (allowlist, comma-separated)</label>
        <input v-model="form.allowedToolsRaw" placeholder="Edit,Bash(git*),Read" />
      </div>

      <div class="field">
        <label>MCP config (.mcp.json, optional)</label>
        <textarea v-model="form.mcpConfigJson" placeholder='{ "mcpServers": { "snipe-it": { "url": "https://…/sse" } } }'></textarea>
      </div>

      <div class="field">
        <label>Container image (optional)</label>
        <input v-model="form.image" placeholder="e.g. python:3.12-bookworm (glibc-based, with bash + git + curl)" />
        <small class="hint">Empty = default image. Claude & the terminal agent are copied in automatically.</small>
      </div>

      <label class="check">
        <input type="checkbox" v-model="form.runAsRoot" />
        Run as root – tools can be installed in the container (apt, npm -g, …)
      </label>

      <div class="grid">
        <div class="field"><label>CPU</label><input v-model="form.cpu" /></div>
        <div class="field"><label>RAM</label><input v-model="form.memory" /></div>
      </div>

      <p v-if="error" class="err">{{ error }}</p>

      <div class="row">
        <button @click="$emit('close')">Cancel</button>
        <button class="primary" :disabled="busy" @click="submit">
          {{ busy ? 'Starting…' : 'Start session' }}
        </button>
      </div>
    </div>
  </div>
</template>

<style scoped>
.overlay {
  position: fixed; inset: 0; background: rgba(5,7,10,.7);
  display: flex; align-items: flex-start; justify-content: center; padding: 24px; overflow-y: auto; z-index: 50;
}
.modal {
  width: 540px; max-width: 100%; background: var(--panel);
  border: 1px solid var(--border); border-radius: 14px; padding: 22px;
}
.modal h3 { margin: 0 0 18px; }
.grid { display: grid; grid-template-columns: 2fr 1fr; gap: 12px; }
.row { display: flex; justify-content: flex-end; gap: 10px; margin-top: 8px; }
.err { color: var(--danger); font-family: var(--mono); font-size: 12px; }
.hint { display: block; margin-top: 4px; color: var(--muted, #8a93a5); font-size: 12px; }
.check { display: flex; align-items: center; gap: 8px; margin: 0 0 14px; font-size: 14px; cursor: pointer; }
.check input { width: auto; }
@media (max-width: 760px) { .grid { grid-template-columns: 1fr; } }
</style>
