<script setup>
// The model of a running session, in its bar (#478, docs/design/live-model-switch.md). Owners and
// collaborators pick from the account's list (#461) or type an id; the hub switches the running
// conversation where the CLI can and confirms it, or stores the pick for the next restart, and
// this says which before anything is pressed. Viewers see the model only.
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { api } from '../../api.js'
import { capabilityText, modelOptions } from '../../lib/model-catalog.js'
import { currentModel, switchErrorText, switchModeNote, validModelId } from '../../lib/model-switch.js'

const props = defineProps({
  session: { type: Object, required: true },
  // Why a switch would be refused now (lib/model-switch.js busyReason), '' when it would not.
  busy: { type: String, default: '' },
  // Injected by tests; the app uses the hub API.
  client: { type: Object, default: () => api }
})
const emit = defineEmits(['switched'])

const view = ref(null)
const open = ref(false)
const draft = ref('')
const applying = ref(false)
const result = ref(null)
const confirmed = ref('')
const listId = `session-models-${Math.random().toString(36).slice(2, 10)}`
const root = ref(null)
const models = computed(() => modelOptions(view.value))
const current = computed(() => currentModel(view.value, confirmed.value))
const mode = computed(() => view.value?.switchMode || 'unsupported')
const canSwitch = computed(() => view.value?.canSwitch === true && mode.value !== 'unsupported')
const choice = computed(() => String(draft.value || '').trim())
const unchanged = computed(() => !!choice.value && choice.value === current.value)
const blocked = computed(() => mode.value === 'live' && !!props.busy)
const ready = computed(() => canSwitch.value && !applying.value && validModelId(choice.value) && !unchanged.value && !blocked.value)

let generation = 0
async function load() {
  const asked = ++generation
  try {
    const answer = await props.client.sessionModels(props.session.id)
    if (asked === generation) view.value = answer
  } catch {
    if (asked === generation) view.value = null
  }
}
watch(() => props.session.id, () => { view.value = null; confirmed.value = ''; result.value = null; open.value = false; void load() })
// A restart or an edit stores another model; the bar follows the session row.
watch(() => props.session.modelName, value => {
  if (view.value) view.value = { ...view.value, selected: value || null }
  if (confirmed.value && confirmed.value !== value && props.session.agent !== 'OpenClaw') confirmed.value = ''
})
onMounted(load)

function toggle() {
  open.value = !open.value
  if (open.value) { draft.value = current.value; result.value = null }
}
async function apply() {
  if (!ready.value) return
  applying.value = true
  result.value = null
  const model = choice.value
  try {
    const outcome = await props.client.switchSessionModel(props.session.id, model)
    if (outcome.mode === 'live') {
      confirmed.value = outcome.model
      result.value = { tone: 'ok', text: `Now on ${outcome.model}.${outcome.confirmation ? ` The agent said: “${outcome.confirmation}”` : ''}` }
    } else {
      result.value = { tone: 'ok', text: `Saved: ${outcome.model} is used from the next restart.` }
    }
    if (view.value && props.session.agent !== 'OpenClaw') view.value = { ...view.value, selected: outcome.model }
    emit('switched', outcome)
  } catch (error) {
    result.value = { tone: 'warn', text: switchErrorText(error?.code, error?.detail), code: error?.code || '' }
  } finally {
    applying.value = false
  }
}
function onKey(event) {
  if (event.key === 'Escape' && open.value) { open.value = false; event.stopPropagation() }
}
function onOutside(event) {
  if (open.value && root.value && !root.value.contains(event.target)) open.value = false
}
onMounted(() => document.addEventListener('pointerdown', onOutside))
onBeforeUnmount(() => { generation += 1; document.removeEventListener('pointerdown', onOutside) })
</script>

<template>
  <div v-if="view" ref="root" class="model-switch" data-model-switch @keydown="onKey">
    <button v-if="canSwitch" type="button" class="chip" data-model-switch-toggle :aria-expanded="open" aria-haspopup="dialog"
      :title="switchModeNote(mode, session.agent)" @click="toggle">
      <span class="label">Model</span><span class="mono value" data-model-current>{{ current || 'CLI default' }}</span><span aria-hidden="true">▾</span>
    </button>
    <span v-else class="chip readonly" data-model-readonly title="The model this session runs on">
      <span class="label">Model</span><span class="mono value" data-model-current>{{ current || 'CLI default' }}</span>
    </span>
    <div v-if="open" class="pop" role="dialog" aria-label="Switch model" data-model-switch-pop>
      <p class="note" :class="mode" data-model-switch-mode>{{ switchModeNote(mode, session.agent) }}</p>
      <ul v-if="models.length" class="options" aria-label="Models this account offers">
        <li v-for="model in models" :key="model.id">
          <button type="button" class="option" :class="{ on: choice === model.id }" :aria-pressed="choice === model.id"
            :data-model-switch-option="model.id" :title="model.description || ''" @click="draft = model.id">
            <span class="mono">{{ model.id }}</span>
            <span v-if="model.id === current" class="tag">current</span>
            <span v-else-if="model.isDefault" class="tag">default</span>
            <span v-if="capabilityText(model)" class="caps">{{ capabilityText(model) }}</span>
          </button>
        </li>
      </ul>
      <p v-else class="note muted">No list for this account yet; type the model id.</p>
      <div class="row">
        <input v-model="draft" class="mono" :list="listId" aria-label="Model id" placeholder="model id" autocomplete="off"
          spellcheck="false" data-model-switch-input @keydown.enter.prevent="apply" />
        <button type="button" class="primary" data-model-switch-apply :disabled="!ready" @click="apply">
          {{ applying ? (mode === 'live' ? 'Switching…' : 'Saving…') : mode === 'live' ? 'Switch' : 'Save for restart' }}</button>
      </div>
      <datalist :id="listId"><option v-for="model in models" :key="model.id" :value="model.id" /></datalist>
      <small v-if="blocked && !applying" class="status warn" data-model-switch-busy>{{ switchErrorText(busy) }}</small>
      <small v-if="result" class="status" :class="result.tone" role="status" data-model-switch-result :data-code="result.code || null">{{ result.text }}</small>
    </div>
  </div>
</template>

<style scoped>
.model-switch { position: relative; display: inline-flex; }
.chip { display: inline-flex; align-items: center; gap: 6px; max-width: 280px; padding: 5px 10px; border: 1px solid var(--border-2); border-radius: 8px; background: none; color: var(--text); font-size: 12px; }
.chip.readonly { color: var(--muted); }
.chip .label { color: var(--muted-3); font-size: 11px; }
.chip .value { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; min-width: 0; }
.pop { position: absolute; z-index: 30; top: calc(100% + 6px); right: 0; width: min(420px, calc(100vw - 32px)); padding: 12px; border: 1px solid var(--border-2); border-radius: 12px; background: var(--panel); box-shadow: 0 8px 28px #0006; }
.note { margin: 0 0 8px; font-size: 11px; line-height: 1.5; color: var(--muted-3); }
.note.restart { color: var(--warn); }
.options { display: flex; flex-direction: column; gap: 3px; max-height: 220px; overflow-y: auto; margin: 0 0 8px; padding: 0; list-style: none; }
.option { display: flex; flex-wrap: wrap; align-items: baseline; gap: 6px; width: 100%; padding: 5px 8px; border: 1px solid transparent; border-radius: 8px; background: none; color: var(--text); font-size: 12px; text-align: left; }
.option:hover { background: var(--panel-2); }
.option.on { border-color: var(--strong); }
.option .mono { overflow-wrap: anywhere; }
.tag { color: var(--ok); font-size: 11px; }
.caps { color: var(--muted-3); font-size: 11px; }
.row { display: flex; gap: 6px; }
.row input { flex: 1 1 auto; min-width: 0; padding: 6px 8px; font-size: 12px; }
.row button { flex: 0 0 auto; padding: 6px 10px; font-size: 12px; border-radius: 8px; }
.status { display: block; margin-top: 8px; font-size: 11px; line-height: 1.5; color: var(--muted-3); }
.status.ok { color: var(--ok); }
.status.warn { color: var(--warn); }
@media (max-width: 640px) { .pop { position: fixed; top: auto; bottom: 12px; left: 16px; right: 16px; width: auto; } }
</style>
