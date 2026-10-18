<script setup>
import { computed } from 'vue'
import { sessionCapabilities } from '../../lib/access.js'
const props = defineProps({ session: { type: Object, required: true }, sharedToken: { type: String, default: null } })
defineEmits(['resume'])
const stopped = computed(() => ['Paused', 'Succeeded', 'Failed', 'Stopped', 'Scheduled'].includes(props.session.phase))
const title = computed(() => props.session.failureReason ? 'Session could not start' :
  ({ Paused: 'Session is paused', Succeeded: 'Session has finished', Failed: 'Session has failed',
    Scheduled: 'Session is waiting for its schedule' }[props.session.phase] || 'Session is stopped'))
const canResume = computed(() => !props.sharedToken && sessionCapabilities(props.session).canManage && props.session.canResume)
</script>
<template>
  <section v-if="stopped" class="stopped-notice" role="status" data-session-stopped>
    <span class="stopped-icon" aria-hidden="true">■</span>
    <div class="stopped-text">
      <strong>{{ title }}</strong>
      <span>The agent is not running. You can still read the conversation below.</span>
    </div>
    <button v-if="canResume" type="button" class="primary" data-stopped-resume @click="$emit('resume', session.id)"><span aria-hidden="true">▶</span> Resume</button>
  </section>
</template>
<style scoped>
.stopped-notice { display: flex; flex: none; align-items: center; gap: 12px; padding: 14px 20px; border-bottom: 1px solid var(--warn); background: var(--panel-2); color: var(--strong); }
.stopped-icon { color: var(--warn); font-size: 18px; }
.stopped-text { display: flex; flex: 1; min-width: 0; flex-direction: column; gap: 4px; }
.stopped-text strong { font-size: 15px; }
.stopped-text > span { font-size: 12px; line-height: 1.5; color: var(--muted); }
button { flex: none; padding: 9px 16px; border-radius: 9px; font-size: 13px; }
@media (max-width: 640px) { .stopped-notice { padding: 12px; gap: 10px; flex-wrap: wrap; } .stopped-text { flex-basis: calc(100% - 30px); } button { margin-left: 28px; } }
</style>
