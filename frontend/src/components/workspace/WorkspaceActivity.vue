<script setup>
import { computed } from 'vue'
const props = defineProps({ state: String, reason: String, source: String, canRespond: Boolean, stalled: Object, canInterrupt: Boolean })
const emit = defineEmits(['interrupt'])
const label = computed(() => props.state === 'working' ? 'Working…' :
  props.reason === 'approval' ? (props.canRespond ? 'Waiting for your approval…' : 'Waiting for approval…')
    : (props.canRespond ? 'Waiting for your answer…' : 'Waiting for an answer…'))
// A turn that stopped progressing without ending (#562, docs/stalled-turns.md). The CLI keeps
// its spinner and timer going, so the Workspace said "Working…" for hours while the messages sent
// in that time waited for a turn end that never came.
const stalledText = computed(() => {
  if (!props.stalled) return ''
  const time = `${props.stalled.minutes} min`
  return props.stalled.reason === 'reconnecting'
    ? `Waiting for the network for ${time}: the agent cannot reach its model and keeps retrying.`
    : `No progress for ${time}: the turn may be stuck.`
})
</script>
<template>
  <div v-if="stalled" class="workspace-activity stalled" role="alert" data-workspace-activity data-stalled :data-reason="stalled.reason">
    <span class="activity-mark" aria-hidden="true"></span>
    <span class="stalled-text">{{ stalledText }}<template v-if="canInterrupt"> Interrupting it sends the messages waiting for the agent.</template></span>
    <button v-if="canInterrupt" type="button" class="stalled-interrupt" data-stalled-interrupt @click="emit('interrupt')">Interrupt</button>
  </div>
  <div v-else-if="state === 'working' || state === 'waiting'" class="workspace-activity" :class="{ working: state === 'working' }"
    role="status" aria-live="polite" aria-atomic="true" data-workspace-activity :data-state="state"
    :title="source === 'output' && state === 'working' ? 'Based on recent terminal output.' : undefined">
    <span class="activity-mark" aria-hidden="true"></span><span>{{ label }}</span>
  </div>
</template>
<style scoped>
.workspace-activity { display: flex; flex: none; align-items: center; gap: 10px; padding: 10px 24px; border-top: 1px solid var(--border); background: var(--panel-2); color: var(--muted); font-size: 13px; }
.activity-mark { width: 12px; height: 12px; flex: none; box-sizing: border-box; border: 2px solid var(--warn); border-radius: 50%; }
.working .activity-mark { border-color: var(--border-2); border-top-color: var(--accent); animation: activity-turn 1s linear infinite; }
.stalled { flex-wrap: wrap; color: var(--text); }
.stalled-text { flex: 1 1 240px; min-width: 0; overflow-wrap: anywhere; }
.stalled-interrupt { flex: none; font-size: 12px; padding: 6px 14px; border-radius: 9px; white-space: nowrap; }
@keyframes activity-turn { to { transform: rotate(360deg); } }
@media (prefers-reduced-motion: reduce) { .working .activity-mark { animation: none; border-color: var(--accent); } }
@media (max-width: 640px) { .workspace-activity { padding-inline: 12px; } }
</style>
