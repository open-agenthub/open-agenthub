<script setup>
import { canPause, sessionStatus, statusStyle } from '../lib/status.js'
import { sessionCapabilities } from '../lib/access.js'

defineProps({ sessions: Array, active: String })
defineEmits(['select', 'remove', 'resume', 'pause', 'edit', 'duplicate', 'share'])
</script>
<template>
  <ul class="list"><li v-for="s in sessions" :key="s.id" :class="{ active: s.id === active }" @click="$emit('select', s.id)">
    <span class="dot" :class="{ ask: s.questionPending }" :style="{ background: statusStyle(s).color }"></span>
    <div class="info">
      <div class="title">{{ s.title }}</div>
      <div class="meta"><span class="st" :style="{ color: statusStyle(s).color }">{{ sessionStatus(s) }}</span><span v-if="s.mode && s.mode !== sessionStatus(s)"> · {{ s.mode }}</span><span v-if="s.accessRole && s.accessRole !== 'Owner'" class="role"> · {{ s.accessRole }}</span><span v-if="s.sharedBy" class="owner"> · by {{ s.sharedBy }}</span><span v-if="s.schedule" class="cron"> · ▶ {{ s.schedule }}</span></div>
    </div>
    <span v-if="sessionCapabilities(s).canManage" class="acts">
      <button v-if="canPause(s)" class="act" title="Pause session" @click.stop="$emit('pause', s.id)">❚❚</button>
      <button v-if="s.canResume" class="act" title="Resume session" @click.stop="$emit('resume', s.id)">▶</button>
      <button class="act" title="Duplicate session" @click.stop="$emit('duplicate', s.id)">⧉</button>
      <button class="act" title="Edit session" @click.stop="$emit('edit', s.id)">✎</button>
      <button class="act" title="Share session" @click.stop="$emit('share', s.id)">↗</button>
      <button class="act del" title="Delete session" @click.stop="$emit('remove', s.id)">✕</button>
    </span>
  </li><li v-if="!sessions.length" class="none">No sessions yet.</li></ul>
</template>
<style scoped>
.list { list-style: none; margin: 0; padding: 0 0 4px; }
.list li { display: flex; align-items: flex-start; gap: 9px; padding: 8px 6px 8px 8px; border-radius: 9px; margin-bottom: 1px; cursor: pointer; }
.list li:hover { background: var(--hover); }
.list li.active { background: var(--panel-2); }
.dot { width: 7px; height: 7px; border-radius: 50%; flex: none; margin-top: 6px; }
.dot.ask { animation: blink 1.1s steps(2, start) infinite; }
@keyframes blink { 50% { opacity: .25; } }
.info { flex: 1; min-width: 0; }
.title { font-size: 13px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.meta { font-size: 11px; color: var(--muted-3); margin-top: 2px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.st { font-weight: 700; }
.role, .owner { color: var(--accent); }
.cron { color: var(--sched); }
.acts { display: flex; gap: 1px; flex-shrink: 0; margin-top: 1px; opacity: 0; transition: opacity .12s; }
li:hover .acts, li.active .acts { opacity: 1; }
.act { width: 20px; height: 20px; display: flex; align-items: center; justify-content: center; font-size: 10px; border-radius: 6px; border: none; background: none; color: var(--muted-3); padding: 0; font-weight: 400; }
.act:hover { background: var(--border-2); color: var(--text); }
.act.del:hover { background: rgba(245,122,106,0.12); color: var(--danger); }
.none { color: var(--muted-3); font-size: 12px; padding: 8px; cursor: default; }
.none:hover { background: none; }
</style>
