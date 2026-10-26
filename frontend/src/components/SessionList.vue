<script setup>
import SessionIcon from './SessionIcon.vue'
import { canPause, sessionStatus, statusStyle } from '../lib/status.js'
import { sessionCapabilities } from '../lib/access.js'
import { authLabel } from '../lib/agent.js'
import { formatRemaining, isExpiringSoon } from '../lib/expiry.js'

defineProps({ sessions: Array, active: String })
defineEmits(['select', 'remove', 'resume', 'pause', 'edit', 'duplicate', 'share', 'organize', 'drag-session', 'drop-session'])
</script>
<template>
  <ul class="list"><li v-for="s in sessions" :key="s.id" :class="{ active: s.id === active }" :data-session-row="s.id" :draggable="sessionCapabilities(s).canManage" @dragstart="$emit('drag-session', $event, s.id)" @dragover.prevent @drop.stop.prevent="$emit('drop-session', $event, s)" @click="$emit('select', s.id)">
    <span class="dot" :class="{ ask: s.questionPending }" :style="{ background: statusStyle(s).color }"></span>
    <div class="info">
      <div class="title">{{ s.title }}</div>
      <!-- Only a marker: the full diagnostic wrapped over a dozen lines here and pushed the other
           sessions out of view. The selected session's Workspace shows it in full. -->
      <div v-if="s.failureReason" class="startup-failure" data-session-failure-marker>Cannot start · open for details</div>
      <div v-if="s.description" class="desc" data-session-description>{{ s.description }}</div>
      <div class="meta"><span class="st" :style="{ color: statusStyle(s).color }">{{ sessionStatus(s) }}</span><span v-if="s.mode && s.mode !== sessionStatus(s)"> · {{ s.mode }}</span><span v-if="s.agent"> · {{ s.agent }}<template v-if="s.authMode"> / {{ authLabel(s.authMode) }}</template></span><span v-if="s.accessRole && s.accessRole !== 'Owner'" class="role"> · {{ s.accessRole }}</span><span v-if="s.sharedBy" class="owner"> · by {{ s.sharedBy }}</span><span v-if="s.schedule" class="cron"> · ▶ {{ s.schedule }}</span><span v-if="s.expiresAt" class="expires" :class="{ soon: isExpiringSoon(s.expiresAt) }" data-expires-at> · <SessionIcon name="expiry" /> {{ formatRemaining(s.expiresAt) }}</span></div>
    </div>
    <span v-if="sessionCapabilities(s).canManage" class="acts">
      <button class="act" title="Organize session" aria-label="Organize session" @click.stop="$emit('organize', s.id)">⋮</button>
      <button v-if="canPause(s)" class="act" title="Pause session" @click.stop="$emit('pause', s.id)">❚❚</button>
      <span v-if="s.outdatedImage" class="status">outdated image</span>
      <button v-if="s.canResume" class="act" title="Resume session" @click.stop="$emit('resume', s.id)">▶</button>
      <button class="act" title="Duplicate session" @click.stop="$emit('duplicate', s.id)" aria-label="Duplicate session"><SessionIcon name="duplicate" /></button>
      <button class="act" title="Edit session" @click.stop="$emit('edit', s.id)">✎</button>
      <button class="act" title="Share session" @click.stop="$emit('share', s.id)">↗</button>
      <button class="act del" title="Delete session" @click.stop="$emit('remove', s.id)">✕</button>
    </span>
  </li><li v-if="!sessions.length" class="none">No sessions yet.</li></ul>
</template>
<style scoped>
.startup-failure { color: var(--danger); font-size: 11px; margin-top: 1px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.list { list-style: none; margin: 0; padding: 0 0 4px; }
.list li { position: relative; display: flex; align-items: flex-start; gap: 9px; padding: 8px 6px 8px 8px; border-radius: 9px; margin-bottom: 1px; cursor: pointer; --row-bg: var(--sidebar); }
.list li:hover, .list li:focus-within { --row-bg: var(--hover); background: var(--row-bg); }
.list li.active { --row-bg: var(--panel-2); background: var(--row-bg); }
.dot { width: 7px; height: 7px; border-radius: 50%; flex: none; margin-top: 6px; }
.dot.ask { animation: blink 1.1s steps(2, start) infinite; }
@keyframes blink { 50% { opacity: .25; } }
.info { flex: 1; min-width: 0; }
.title { font-size: 13px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.desc { font-size: 11px; color: var(--muted-2); margin-top: 1px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.meta { font-size: 11px; color: var(--muted-3); margin-top: 2px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.st { font-weight: 700; }
.role, .owner { color: var(--accent); }
.cron { color: var(--sched); }
.expires { color: var(--muted); }
.expires.soon { color: var(--warn); font-weight: 700; }
/* The actions float over the row instead of taking part in its flex layout. In the flow, the
   strip kept its ~104px while invisible, so every title was cut to a few characters unless the
   sidebar was dragged very wide (#179). Hidden, it ignores the pointer so a click on the end of
   a title opens the session instead of hitting an invisible Delete. */
.acts { position: absolute; top: 7px; right: 6px; display: flex; gap: 1px; padding-left: 14px; background: linear-gradient(to right, transparent, var(--row-bg) 12px); opacity: 0; pointer-events: none; transition: opacity .12s; }
li:hover .acts, li:focus-within .acts { opacity: 1; pointer-events: auto; }
/* Without hover (touch) the selected row is the only way to reach them. */
@media (hover: none) { li.active .acts { opacity: 1; pointer-events: auto; } }
.act { width: 20px; height: 20px; display: flex; align-items: center; justify-content: center; font-size: 10px; border-radius: 6px; border: none; background: none; color: var(--muted-3); padding: 0; font-weight: 400; }
.act:hover { background: var(--border-2); color: var(--text); }
.act.del:hover { background: rgba(245,122,106,0.12); color: var(--danger); }
.none { color: var(--muted-3); font-size: 12px; padding: 8px; cursor: default; }
.none:hover { background: none; }
</style>
