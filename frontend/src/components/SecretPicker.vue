<script setup>
// Which stored secrets a session receives (docs/session-secrets.md). Inside a project the options
// are what the project selects and everything ticked means "follow the project"; outside a
// project the options are every stored secret and nothing is ticked until the owner ticks it.
import { deliveryLabel } from '../lib/secrets.js'

const props = defineProps({
  // {id, name, delivery, description} as the secret listing returns them — never a value.
  options: { type: Array, default: () => [] },
  modelValue: { type: Array, default: () => [] },
  inProject: { type: Boolean, default: false },
  // Whether the project selects any secret at all; with none there is nothing to pick from.
  projectHasNone: { type: Boolean, default: false }
})
const emit = defineEmits(['update:modelValue'])

function toggle(id) {
  const next = props.modelValue.includes(id)
    ? props.modelValue.filter(item => item !== id)
    : [...props.modelValue, id]
  emit('update:modelValue', next)
}
</script>

<template>
  <div v-if="options.length" class="field secrets" data-secret-picker>
    <label>Secrets <span class="dim">— delivered as environment variables or files in ~/.secrets</span></label>
    <label v-for="secret in options" :key="secret.id" class="check">
      <input type="checkbox" :checked="modelValue.includes(secret.id)" :data-secret-option="secret.id" @change="toggle(secret.id)" />
      <span><code>{{ secret.name }}</code> <span class="kind">{{ deliveryLabel(secret.delivery) }}</span><span v-if="secret.description" class="dim desc">{{ secret.description }}</span></span>
    </label>
    <small class="hint" data-secret-hint>
      <template v-if="inProject">With every secret ticked the session follows the project's selection, including later changes; untick one to keep it out of this session.</template>
      <template v-else>Outside a project a session gets only what is ticked here.</template>
      Files reach a running session within about a minute, variables at its next start.
    </small>
  </div>
  <small v-else-if="inProject && projectHasNone" class="hint none" data-secret-none>This project selects no secrets.</small>
</template>

<style scoped>
.secrets { margin-top: 14px; }
.dim { color: var(--faint); font-weight: 400; }
.desc { margin-left: 6px; }
.check { display: flex; align-items: center; gap: 10px; margin: 4px 0 0; font-size: 13px; color: var(--text); cursor: pointer; }
.check input { width: auto; }
code { font-family: var(--mono); font-size: 12px; }
.kind { display: inline-block; margin-left: 6px; padding: 1px 6px; border: 1px solid var(--border-2); border-radius: 4px; color: var(--muted-3); font-size: 11px; font-weight: 700; }
.hint { display: block; margin-top: 6px; color: var(--muted-3); font-size: 12px; }
.none { margin-top: 14px; }
</style>
