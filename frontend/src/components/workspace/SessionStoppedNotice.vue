<script setup>
import { computed } from 'vue'
import { sessionCapabilities } from '../../lib/access.js'
import { isSessionStopped } from '../../lib/status.js'
const props = defineProps({ session: { type: Object, required: true }, sharedToken: { type: String, default: null } })
defineEmits(['resume'])
const stopped = computed(() => isSessionStopped(props.session))
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
      <a v-if="session.failureReason" href="#startup-failure-title">View startup failure details</a>
      <span v-if="session.mode === 'Scheduled' && session.nextRunAt" data-scheduled-next-run>Next planned run (UTC): {{ new Date(session.nextRunAt).toISOString().replace('T', ' ').replace('.000Z', '') }}. A previous Job may delay it.</span>
      <span>The agent is not running. Your conversation stays readable above.</span>
    </div>
    <button v-if="canResume" type="button" class="primary" data-stopped-resume @click="$emit('resume', session.id)"><span aria-hidden="true">▶</span> Resume</button>
  </section>
</template>
<style scoped>
.stopped-notice { display: flex; flex: none; align-items: center; gap: 12px; padding: 14px 20px; border-top: 1px solid var(--warn); background: var(--panel-2); color: var(--strong); }
.stopped-icon { color: var(--warn); font-size: 18px; }
.stopped-text { display: flex; flex: 1; min-width: 0; flex-direction: column; gap: 4px; }
.stopped-text strong { font-size: 15px; }
.stopped-text a { color: var(--accent); font-size: 12px; }
.stopped-text > span { font-size: 12px; line-height: 1.5; color: var(--muted); }
button { flex: none; padding: 9px 16px; border-radius: 9px; font-size: 13px; }
@media (max-width: 640px) { .stopped-notice { padding: 12px; gap: 10px; flex-wrap: wrap; } .stopped-text { flex-basis: calc(100% - 30px); } button { margin-left: 28px; } }
</style>
