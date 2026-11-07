<script setup>
// The model a session starts on (#461). Free text with the account's list as suggestions: the
// list may be unknown, stale or refused, and a person who knows an id must never be blocked by
// it. Nothing here switches the chosen model when a list changes.
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { api } from '../api.js'
import { capabilityText, catalogQuery, catalogStatus, modelOptions, unlistedNote } from '../lib/model-catalog.js'

const props = defineProps({
  agent: { type: String, required: true },
  authMode: { type: String, required: true },
  credentialId: { type: String, default: '' },
  modelEndpointUrl: { type: String, default: '' },
  modelValue: { type: String, default: '' },
  placeholder: { type: String, default: 'default model' },
  // Injected by tests; the app uses the hub API.
  client: { type: Object, default: () => api }
})
const emit = defineEmits(['update:modelValue'])

const view = ref(null)
const loading = ref(false)
const notice = ref('')
const listId = `model-options-${Math.random().toString(36).slice(2, 10)}`
const query = computed(() => catalogQuery(props))
const models = computed(() => modelOptions(view.value))
const status = computed(() => catalogStatus(view.value))
const unlisted = computed(() => unlistedNote(view.value, props.modelValue))
const chosen = computed(() => models.value.find(model => model.id === String(props.modelValue || '').trim()) || null)
let timer = null
let generation = 0

async function load() {
  const current = query.value
  const asked = ++generation
  notice.value = ''
  if (!current) { view.value = null; return }
  loading.value = true
  try {
    const answer = await props.client.modelCatalog(current)
    if (asked === generation) view.value = answer
  } catch {
    if (asked === generation) view.value = null
  } finally {
    if (asked === generation) loading.value = false
  }
}

async function refresh() {
  const current = query.value
  if (!current) return
  const asked = ++generation
  loading.value = true
  notice.value = ''
  try {
    const answer = await props.client.refreshModelCatalog(current)
    if (asked === generation) view.value = answer
  } catch (error) {
    if (asked !== generation) return
    notice.value = error?.status === 409
      ? 'No running session uses this account, so nothing could ask its CLI. Start one, or type the model id.'
      : 'The refresh failed; the list below is the last one known.'
  } finally {
    if (asked === generation) loading.value = false
  }
}

// An endpoint URL is typed character by character; one request per pause, not per key.
watch(() => JSON.stringify(query.value), () => {
  clearTimeout(timer)
  if (props.authMode === 'CustomEndpoint') timer = setTimeout(load, 400)
  else load()
}, { immediate: true })
onBeforeUnmount(() => clearTimeout(timer))
</script>

<template>
  <div class="model-picker" data-model-picker>
    <div class="row">
      <input data-model-input class="mono" :value="modelValue" :list="listId" aria-label="Model"
        :placeholder="placeholder" autocomplete="off" spellcheck="false"
        @input="emit('update:modelValue', $event.target.value)" />
      <button type="button" class="ghost" data-model-refresh :disabled="!query || loading" @click="refresh">
        {{ loading ? 'Reading…' : 'Refresh' }}</button>
    </div>
    <datalist :id="listId">
      <option v-for="model in models" :key="model.id" :value="model.id">{{ model.label || model.id }}{{ model.isDefault ? ' (default)' : '' }}</option>
    </datalist>
    <ul v-if="models.length" class="options" data-model-options aria-label="Models this account offers">
      <li v-for="model in models" :key="model.id">
        <button type="button" class="option" :class="{ on: chosen?.id === model.id }" :aria-pressed="chosen?.id === model.id"
          :data-model-option="model.id" :title="model.description || ''" @click="emit('update:modelValue', model.id)">
          <span class="mono">{{ model.id }}</span><span v-if="model.isDefault" class="tag">default</span>
          <span v-if="capabilityText(model)" class="caps">{{ capabilityText(model) }}</span>
        </button>
      </li>
    </ul>
    <small class="status" :class="status.tone" data-model-status aria-live="polite">{{ status.text }}</small>
    <small v-if="unlisted" class="status warn" data-model-unlisted>{{ unlisted }}</small>
    <small v-if="notice" class="status warn" data-model-notice>{{ notice }}</small>
  </div>
</template>

<style scoped>
.row { display: flex; gap: 6px; }
.row input { flex: 1 1 auto; min-width: 0; }
.ghost { flex: 0 0 auto; padding: 6px 10px; border: 1px solid var(--border-2); border-radius: 8px; background: none; color: var(--muted-3); font-size: 12px; }
.ghost:disabled { opacity: .5; }
.options { display: flex; flex-wrap: wrap; gap: 4px; max-height: 132px; overflow-y: auto; margin: 6px 0 0; padding: 0; list-style: none; }
.option { display: inline-flex; flex-wrap: wrap; align-items: baseline; gap: 6px; max-width: 100%; padding: 4px 8px; border: 1px solid var(--border-2); border-radius: 8px; background: var(--panel); color: var(--text); font-size: 12px; text-align: left; }
.option.on { border-color: var(--strong); }
.option .mono { overflow-wrap: anywhere; }
.tag { color: var(--ok); font-size: 11px; }
.caps { color: var(--muted-3); font-size: 11px; }
small { display: block; margin-top: 5px; font-size: 11px; color: var(--muted-3); }
.status.ok { color: var(--muted-3); }
.status.warn { color: var(--warn); }
</style>
