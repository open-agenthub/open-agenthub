<script setup>
defineProps({ sessions: Array, active: String })
defineEmits(['select', 'remove', 'resume'])

const phaseColor = {
  Running: 'var(--running)', Pending: 'var(--accent)', Scheduled: 'var(--muted)',
  Succeeded: 'var(--ok)', Failed: 'var(--danger)'
}
const modeLabel = { Interactive: 'interactive', Autonomous: 'autonomous', Scheduled: 'scheduled' }
</script>

<template>
  <ul class="list">
    <li v-for="s in sessions" :key="s.id"
        :class="{ active: s.id === active }"
        @click="$emit('select', s.id)">
      <span class="dot" :class="{ ask: s.questionPending }"
            :style="{ background: s.questionPending ? 'var(--accent)' : (phaseColor[s.phase] || 'var(--muted)') }"></span>
      <div class="info">
        <div class="title">{{ s.title }}</div>
        <div class="meta">
          <span class="tag">{{ modeLabel[s.mode] || s.mode }}</span>
          <span class="phase">{{ s.phase }}</span>
          <span v-if="s.questionPending" class="ask-label">waiting for reply</span>
          <span v-if="s.schedule" class="cron">{{ s.schedule }}</span>
        </div>
        <div v-if="s.repoUrl" class="repo">{{ s.repoUrl }}</div>
      </div>
      <button v-if="s.canResume" class="resume" title="Resume"
              @click.stop="$emit('resume', s.id)">↻</button>
      <button class="x" title="Delete" @click.stop="$emit('remove', s.id)">✕</button>
    </li>
    <li v-if="!sessions.length" class="none">No sessions yet.</li>
  </ul>
</template>

<style scoped>
.list { list-style: none; margin: 0; padding: 0 8px 16px; overflow-y: auto; }
.list li {
  display: flex; align-items: center; gap: 11px; padding: 12px;
  border: 1px solid transparent; border-radius: var(--radius); margin-bottom: 6px; cursor: pointer;
}
.list li:hover { background: var(--panel-2); }
.list li.active { background: var(--panel-2); border-color: var(--accent); }
.dot { width: 10px; height: 10px; border-radius: 50%; flex: none; }
.dot.ask { animation: blink 1.1s steps(2, start) infinite; }
@keyframes blink { 50% { opacity: .25; } }
.info { flex: 1; min-width: 0; }
.title { font-weight: 600; font-size: 14px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.meta { display: flex; gap: 8px; align-items: center; margin-top: 3px; font-size: 11px; color: var(--muted); font-family: var(--mono); flex-wrap: wrap; }
.tag { color: var(--accent); }
.cron { color: var(--running); }
.ask-label { color: var(--accent); }
.repo { font-size: 11px; color: var(--muted); font-family: var(--mono); margin-top: 3px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.resume, .x { background: none; border: none; color: var(--muted); padding: 4px 6px; font-size: 14px; }
.resume:hover { color: var(--running); border: none; }
.x:hover { color: var(--danger); border: none; }
.none { color: var(--muted); font-size: 13px; padding: 12px; justify-content: center; }
</style>
