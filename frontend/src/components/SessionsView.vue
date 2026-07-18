<script setup>
import { computed, ref } from 'vue'
import { canPause, sessionStatus, statusStyle } from '../lib/status.js'
import { sessionCapabilities } from '../lib/access.js'
import { repoShortName, sessionMatches } from '../lib/text.js'

const props = defineProps({
  sessions: { type: Array, default: () => [] },
  projects: { type: Array, default: () => [] },
  query: { type: String, default: '' }
})
const emit = defineEmits(['select', 'new', 'remove', 'resume', 'pause', 'edit', 'duplicate'])

const GROUP_BYS = ['Project', 'Repository', 'Status', 'None']
const FILTERS = ['All', 'Running', 'Waiting', 'Scheduled', 'Paused', 'Done', 'Failed']
const groupBy = ref('Project')
const filter = ref('All')

const projectName = computed(() => Object.fromEntries(props.projects.map(p => [p.id, p.name])))

function groupKey(s) {
  switch (groupBy.value) {
    case 'Repository': return repoShortName(s.repoUrl || s.repos?.[0]?.url || '') || 'No repository'
    case 'Status': return sessionStatus(s)
    case 'None': return 'All sessions'
    default:
      if (s.accessRole && s.accessRole !== 'Owner') return 'Shared with me'
      return projectName.value[s.projectId] || 'No project'
  }
}

const filtered = computed(() => props.sessions
  .filter(s => sessionMatches(s, props.query))
  .filter(s => filter.value === 'All' || sessionStatus(s) === filter.value))

const groups = computed(() => {
  const names = [...new Set(filtered.value.map(groupKey))]
  return names.map(name => ({ name, sessions: filtered.value.filter(s => groupKey(s) === name) }))
})

const counts = computed(() => ({
  total: props.sessions.length,
  running: props.sessions.filter(s => ['Running', 'Pending'].includes(s.phase)).length,
  waiting: props.sessions.filter(s => s.questionPending).length
}))

function repoLine(s) {
  const repo = repoShortName(s.repoUrl || s.repos?.[0]?.url || '')
  return [repo, s.schedule].filter(Boolean).join(' · ')
}
</script>
<template>
  <div class="sessions-page">
    <div class="head">
      <div>
        <h2>Sessions</h2>
        <div class="sub">{{ counts.total }} total · {{ counts.running }} running<template v-if="counts.waiting"> · {{ counts.waiting }} waiting on you</template></div>
      </div>
      <div class="group-by">
        <span class="gb-label">Group by</span>
        <button v-for="g in GROUP_BYS" :key="g" class="chip" :class="{ on: groupBy === g }" @click="groupBy = g">{{ g }}</button>
      </div>
    </div>
    <div class="filters">
      <button v-for="f in FILTERS" :key="f" class="chip filter" :class="{ on: filter === f }" @click="filter = f">{{ f }}</button>
    </div>
    <div v-for="grp in groups" :key="grp.name" class="group">
      <div class="group-name">{{ grp.name.toUpperCase() }}</div>
      <div class="card rows">
        <div v-for="s in grp.sessions" :key="s.id" class="row" @click="$emit('select', s.id)">
          <div class="row-status">
            <span class="st" :style="{ color: statusStyle(s).color }">{{ sessionStatus(s) }}</span>
            <span class="mode">{{ s.mode }}</span>
          </div>
          <div class="row-main">
            <div class="row-title">{{ s.title }}</div>
            <div class="row-repo">{{ repoLine(s) }}<span v-if="s.sharedBy" class="shared-by"> · by {{ s.sharedBy }}</span></div>
          </div>
          <div v-if="sessionCapabilities(s).canManage" class="row-actions">
            <button class="act" title="Duplicate session" @click.stop="$emit('duplicate', s.id)">⧉</button>
            <button v-if="canPause(s)" class="act" title="Pause session" @click.stop="$emit('pause', s.id)">❚❚</button>
            <button v-if="s.canResume" class="act" title="Resume session" @click.stop="$emit('resume', s.id)">▶</button>
            <button class="act" title="Edit session" @click.stop="$emit('edit', s.id)">✎</button>
            <button class="act del" title="Delete session" @click.stop="$emit('remove', s.id)">✕</button>
          </div>
        </div>
      </div>
    </div>
    <div v-if="!groups.length" class="empty">
      <p>No sessions match.</p>
      <button class="primary" @click="$emit('new')">+ Give an agent a task</button>
    </div>
  </div>
</template>
<style scoped>
.sessions-page { flex: 1; min-width: 0; padding: 26px 28px; display: flex; flex-direction: column; gap: 18px; overflow-y: auto; }
.head { display: flex; align-items: flex-end; justify-content: space-between; flex-wrap: wrap; gap: 12px; }
h2 { font-size: 28px; font-weight: 700; margin: 0; }
.sub { font-size: 14px; color: var(--muted-2); margin-top: 4px; }
.group-by { display: flex; align-items: center; gap: 6px; flex-wrap: wrap; }
.gb-label { font-size: 12px; color: var(--muted-3); margin-right: 2px; }
.chip { font-size: 12px; font-weight: 600; padding: 6px 12px; border-radius: 999px; border: 1px solid transparent; background: none; color: var(--muted-3); }
.chip:hover { color: var(--text); background: var(--hover); }
.chip.on { background: var(--panel-2); color: var(--text); border-color: var(--border-3); }
.filters { display: flex; gap: 8px; flex-wrap: wrap; }
.chip.filter { background: var(--panel); border-color: var(--border-2); color: var(--muted); }
.chip.filter.on { background: rgba(90,169,245,0.14); color: var(--accent-2); border-color: rgba(90,169,245,0.4); }
.group { display: flex; flex-direction: column; gap: 8px; }
.group-name { font-size: 12px; font-weight: 700; letter-spacing: 0.08em; color: var(--muted-3); padding: 4px 2px 0; }
.rows { overflow: hidden; }
.row { padding: 12px 18px; display: flex; align-items: center; gap: 12px; cursor: pointer; }
.row + .row { border-top: 1px solid var(--border); }
.row:hover { background: var(--hover); }
.row-status { width: 88px; flex-shrink: 0; display: flex; flex-direction: column; gap: 2px; }
.st { font-size: 11px; font-weight: 700; }
.mode { font-size: 11px; color: var(--muted-3); font-weight: 600; }
.row-main { flex: 1; min-width: 0; }
.row-title { font-weight: 600; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.row-repo { font-family: var(--mono); font-size: 12px; color: var(--muted-3); margin-top: 2px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.shared-by { color: var(--accent); }
.row-actions { display: flex; gap: 4px; flex-shrink: 0; }
.act { width: 28px; height: 28px; display: flex; align-items: center; justify-content: center; font-size: 13px; border-radius: 8px; border: none; background: none; color: var(--muted); padding: 0; }
.act:hover { background: var(--panel-2); color: var(--text); }
.act.del:hover { background: rgba(245,122,106,0.12); color: var(--danger); }
.empty { margin: 40px auto; text-align: center; color: var(--muted); }
</style>
