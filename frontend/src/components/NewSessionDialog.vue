<script setup>
import { ref, computed } from 'vue'
import { api } from '../api.js'
import RepoPicker from './RepoPicker.vue'

const emit = defineEmits(['close', 'created'])
const props = defineProps({ embedded: { type: Boolean, default: false }, projects: { type: Array, default: () => [] } })

const MODES = [
  { key: 'Interactive', hint: 'watch & reply' },
  { key: 'Autonomous', hint: 'works through a prompt on its own' },
  { key: 'Scheduled', hint: 'runs on a schedule (CronJob)' }
]
const repos = ref([])
const advOpen = ref(false)
const form = ref({
  title: '',
  mode: 'Interactive',
  prompt: '',
  schedule: '0 6 * * 1-5',
  projectId: '',
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
const modeHint = computed(() => MODES.find(m => m.key === form.value.mode)?.hint)

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
      projectId: form.value.projectId || null,
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
      <h3 class="form-title">New session</h3>

      <div class="card sect">
        <div class="field">
          <label>Title</label>
          <input v-model="form.title" placeholder="e.g. Resolve ticket OPS-1423" />
        </div>
        <div class="field">
          <label>Mode</label>
          <div class="chips-box">
            <button v-for="m in MODES" :key="m.key" class="chip" :class="{ on: form.mode === m.key }" @click="form.mode = m.key">{{ m.key }}</button>
          </div>
          <small class="hint">{{ modeHint }}</small>
        </div>
        <div class="field" v-if="needsSchedule">
          <label>Schedule <span class="dim">— cron, UTC</span></label>
          <input v-model="form.schedule" class="mono short" placeholder="0 6 * * 1-5" />
        </div>
        <div class="field" v-if="needsPrompt">
          <label>Task <span class="dim">— what should the agent do?</span></label>
          <textarea v-model="form.prompt" class="task" placeholder="Describe the task in plain language — the agent figures out the rest."></textarea>
        </div>
        <div class="field last">
          <label>Project</label>
          <select v-model="form.projectId"><option value="">No project</option><option v-for="project in projects" :key="project.id" :value="project.id">{{ project.name }}</option></select>
        </div>
      </div>

      <div class="card sect">
        <label>Repositories <span class="dim">— optional, pick one or more</span></label>
        <RepoPicker v-model="repos" />
      </div>

      <div class="card adv">
        <button class="adv-head" @click="advOpen = !advOpen">
          <span><b>Advanced</b><span class="dim adv-sub">tools, MCP, container, resources</span></span>
          <span class="dim">{{ advOpen ? '▾' : '▸' }}</span>
        </button>
        <div v-if="advOpen" class="adv-body">
          <div class="field" v-if="needsPrompt">
            <label>Allowed tools <span class="dim">— allowlist, comma-separated</span></label>
            <input v-model="form.allowedToolsRaw" class="mono" placeholder="Edit,Bash(git*),Read" />
          </div>
          <div class="field">
            <label>Extra tools <span class="dim">— MCP servers the agent can use (.mcp.json)</span></label>
            <textarea v-model="form.mcpConfigJson" placeholder='{ "mcpServers": { "snipe-it": { "url": "https://…/sse" } } }'></textarea>
          </div>
          <div class="grid3">
            <div class="field"><label>Container image</label><input v-model="form.image" class="mono" placeholder="default agent image" /></div>
            <div class="field"><label>CPU</label><input v-model="form.cpu" class="mono" /></div>
            <div class="field"><label>Memory</label><input v-model="form.memory" class="mono" /></div>
          </div>
          <label class="check">
            <input type="checkbox" v-model="form.runAsRoot" />
            <span><b>Run as root</b> — only if the task needs system packages (apt, npm&nbsp;-g, …)</span>
          </label>
        </div>
      </div>

      <p v-if="error" class="err">{{ error }}</p>

      <div class="row">
        <button class="primary" :disabled="busy" @click="submit">{{ busy ? 'Starting…' : 'Start session' }}</button>
        <button @click="$emit('close')">Cancel</button>
        <span class="dim note-inline">Claude &amp; the terminal agent are set up in the container automatically.</span>
      </div>
    </div>
  </div>
</template>

<style scoped>
.overlay { position: fixed; inset: 0; background: rgba(10,9,8,.7); display: flex; align-items: flex-start; justify-content: center; padding: 24px; overflow-y: auto; z-index: 50; }
.modal { width: 680px; max-width: 100%; background: var(--panel); border: 1px solid var(--border-2); border-radius: var(--radius-lg); padding: 22px; }
.embed-inner { max-width: 720px; }
.form-title { font-size: 20px; margin: 0 0 16px; }
.sect { padding: 18px 20px; margin-bottom: 16px; }
.field.last { margin-bottom: 0; }
.chips-box { display: inline-flex; gap: 2px; background: var(--input); border: 1px solid var(--border-2); border-radius: var(--radius); padding: 3px; }
.chip { font-size: 12px; font-weight: 700; padding: 6px 14px; border-radius: 8px; border: none; background: none; color: var(--muted-3); }
.chip:hover { color: var(--text); background: none; }
.chip.on { background: var(--border-2); color: var(--strong); }
.hint { display: block; margin-top: 6px; color: var(--muted-3); font-size: 12px; }
.dim { color: var(--faint); font-weight: 400; }
.mono { font-family: var(--mono); font-size: 13px; }
.short { width: 200px; }
.task { font-family: var(--ui); font-size: 14px; min-height: 90px; }
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
