<script setup>
// Request and limit of CPU and memory, shared by the new, edit and duplicate dialogs
// (docs/session-resources.md). An empty limit means the instance default.
import { computed } from 'vue'
import { resourceSummary } from '../lib/resources.js'

const props = defineProps({
  modelValue: { type: Object, required: true },
  // The session being edited or copied; its effective values are shown under the fields.
  session: { type: Object, default: null }
})
const emit = defineEmits(['update:modelValue'])

function set(key, value) { emit('update:modelValue', { ...props.modelValue, [key]: value }) }

// What a limit left empty resolves to. Only known for a session that sets none itself: then its
// effective limit is the instance default.
const defaultCpuLimit = computed(() => props.session && !props.session.cpuLimit ? props.session.resources?.cpuLimit : '')
const defaultMemoryLimit = computed(() => props.session && !props.session.memoryLimit ? props.session.resources?.memoryLimit : '')
const effective = computed(() => resourceSummary(props.session?.resources))
</script>

<template>
  <div class="resources" data-session-resources-fields>
    <div class="grid4">
      <div class="field"><label>CPU request</label>
        <input :value="modelValue.cpu" class="mono" placeholder="500m" data-resource="cpu" @input="set('cpu', $event.target.value)" /></div>
      <div class="field"><label>CPU limit</label>
        <input :value="modelValue.cpuLimit" class="mono" :placeholder="defaultCpuLimit ? `default (${defaultCpuLimit})` : 'default'"
          data-resource="cpuLimit" @input="set('cpuLimit', $event.target.value)" /></div>
      <div class="field"><label>Memory request</label>
        <input :value="modelValue.memory" class="mono" placeholder="1Gi" data-resource="memory" @input="set('memory', $event.target.value)" /></div>
      <div class="field"><label>Memory limit</label>
        <input :value="modelValue.memoryLimit" class="mono" :placeholder="defaultMemoryLimit ? `default (${defaultMemoryLimit})` : 'default'"
          data-resource="memoryLimit" @input="set('memoryLimit', $event.target.value)" /></div>
    </div>
    <p class="hint">The request is reserved on the node; above the limit CPU is throttled and the agent is killed for memory. Memory is given in Mi or Gi; an empty limit is the instance default.</p>
    <p v-if="effective" class="hint" data-session-resources-effective>
      Effective: {{ effective }}<template v-if="session?.resourcesPendingRestart"> — from the next restart; the running pod still has its previous values</template>
    </p>
  </div>
</template>

<style scoped>
.grid4 { display: grid; grid-template-columns: repeat(4, 1fr); gap: 14px; }
.mono { font-family: var(--mono); font-size: 13px; }
.hint { margin: 0 0 10px; color: var(--muted-2); font-size: 12px; line-height: 1.5; }
@media (max-width: 760px) { .grid4 { grid-template-columns: 1fr 1fr; } }
</style>
