<script setup>
import { computed } from 'vue'
const props = defineProps({ state: String, reason: String, source: String, canRespond: Boolean })
const label = computed(() => props.state === 'working' ? 'Working…' :
  props.reason === 'approval' ? (props.canRespond ? 'Waiting for your approval…' : 'Waiting for approval…')
    : (props.canRespond ? 'Waiting for your answer…' : 'Waiting for an answer…'))
</script>
<template>
  <div v-if="state === 'working' || state === 'waiting'" class="workspace-activity" :class="{ working: state === 'working' }"
    role="status" aria-live="polite" aria-atomic="true" data-workspace-activity :data-state="state"
    :title="source === 'output' && state === 'working' ? 'Based on recent terminal output.' : undefined">
    <span class="activity-mark" aria-hidden="true"></span><span>{{ label }}</span>
  </div>
</template>
<style scoped>
.workspace-activity { display: flex; flex: none; align-items: center; gap: 10px; padding: 10px 24px; border-top: 1px solid var(--border); background: var(--panel-2); color: var(--muted); font-size: 13px; }
.activity-mark { width: 12px; height: 12px; box-sizing: border-box; border: 2px solid var(--warn); border-radius: 50%; }
.working .activity-mark { border-color: var(--border-2); border-top-color: var(--accent); animation: activity-turn 1s linear infinite; }
@keyframes activity-turn { to { transform: rotate(360deg); } }
@media (prefers-reduced-motion: reduce) { .working .activity-mark { animation: none; border-color: var(--accent); } }
@media (max-width: 640px) { .workspace-activity { padding-inline: 12px; } }
</style>
