<script setup>
// One secret's level as a segmented radio group — the control every secret selection uses (#329):
// the session's three levels and the project's two.
const props = defineProps({
  levels: { type: Array, required: true },
  modelValue: { type: String, required: true },
  // The id of the element naming the secret.
  labelledby: { type: String, required: true },
  disabled: { type: Boolean, default: false }
})
const emit = defineEmits(['update:modelValue'])

function choose(level) {
  if (props.disabled || level === props.modelValue) return
  emit('update:modelValue', level)
}
// Radio-group keyboard behaviour: arrows move and select, Home/End jump, and only the checked
// option is in the tab order, so tabbing through a long list costs one stop per secret.
function onKey(event) {
  const index = props.levels.findIndex(l => l.value === props.modelValue)
  const step = { ArrowRight: 1, ArrowDown: 1, ArrowLeft: -1, ArrowUp: -1 }[event.key]
  let next
  if (step) next = (index + step + props.levels.length) % props.levels.length
  else if (event.key === 'Home') next = 0
  else if (event.key === 'End') next = props.levels.length - 1
  else return
  event.preventDefault()
  choose(props.levels[next].value)
  event.currentTarget.parentElement.children[next]?.focus()
}
</script>

<template>
  <div class="segmented" role="radiogroup" :aria-labelledby="labelledby" :aria-disabled="disabled || null">
    <button v-for="level in levels" :key="level.value" type="button" role="radio"
      :class="{ on: modelValue === level.value }"
      :aria-checked="modelValue === level.value"
      :tabindex="modelValue === level.value ? 0 : -1"
      :disabled="disabled"
      :data-grant-level="level.value"
      @click="choose(level.value)" @keydown="onKey">{{ level.label }}</button>
  </div>
</template>

<style scoped>
/* The look of the session bar's Workspace | Terminal | Shell switch (TerminalView .tabs). */
.segmented { display: flex; flex-wrap: wrap; gap: 2px; max-width: 100%; background: var(--panel); border: 1px solid var(--border-2); border-radius: var(--radius); padding: 3px; }
.segmented button { width: auto; font-size: 12px; font-weight: 700; padding: 5px 12px; border-radius: 8px; border: none; background: none; color: var(--muted-3); white-space: nowrap; }
.segmented button:hover:not(:disabled):not(.on) { color: var(--text); background: none; }
.segmented button.on { background: var(--border-2); color: var(--strong); }
.segmented button:disabled { cursor: default; }
</style>
