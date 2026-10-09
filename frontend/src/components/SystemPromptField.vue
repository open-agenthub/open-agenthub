<script setup>
import { computed } from 'vue'

// Mirrors SessionSystemPrompt.MaxLength in the backend. The cap exists because the text travels
// as a pod environment variable; enforcing it here turns a 400 after submit into a counter.
const SYSTEM_PROMPT_MAX = 20000

const props = defineProps({
  modelValue: { type: String, default: '' },
  // A scheduled session's CronJob carries the prompt in its pod template, so the backend
  // rejects a change; the field stays visible so the rules are not hidden, just not editable.
  readonly: { type: Boolean, default: false },
  readonlyHint: { type: String, default: '' }
})
defineEmits(['update:modelValue'])

const length = computed(() => (props.modelValue || '').length)
const nearCap = computed(() => length.value >= SYSTEM_PROMPT_MAX * 0.9)
</script>

<template>
  <div class="field">
    <label>System prompt <span class="dim">— standing rules for this session, optional</span></label>
    <textarea :value="modelValue" data-system-prompt class="prompt" :maxlength="SYSTEM_PROMPT_MAX" :readonly="readonly"
      placeholder="e.g. You review, you do not commit. Answer in short bullet points."
      @input="$emit('update:modelValue', $event.target.value)"></textarea>
    <div class="meta">
      <small class="hint">{{ readonly && readonlyHint ? readonlyHint : 'Appended to the agent’s own instructions; applies on the next start or resume.' }}</small>
      <small class="count" :class="{ near: nearCap }" data-system-prompt-count>{{ length.toLocaleString() }} / {{ SYSTEM_PROMPT_MAX.toLocaleString() }}</small>
    </div>
  </div>
</template>

<style scoped>
.prompt { font-family: var(--ui); font-size: 14px; min-height: 90px; }
.prompt[readonly] { color: var(--muted-2); cursor: default; }
.meta { display: flex; justify-content: space-between; gap: 12px; margin-top: 6px; }
.hint { color: var(--muted-3); font-size: 12px; }
.count { flex-shrink: 0; color: var(--faint); font-family: var(--mono); font-size: 11px; }
.count.near { color: var(--warn); }
.dim { color: var(--faint); font-weight: 400; }
</style>
