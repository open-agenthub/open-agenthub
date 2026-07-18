<script setup>
import { computed, onMounted, ref } from 'vue'
import { api } from '../api.js'
import { sessionStatus, statusStyle } from '../lib/status.js'
import { formatCost, formatTokens, totalTokens } from '../lib/usage.js'
import { repoShortName } from '../lib/text.js'

const props = defineProps({ sessions: { type: Array, default: () => [] } })
const emit = defineEmits(['select', 'sessions', 'usage', 'new', 'resume'])

const summary = ref(null)
const usageRows = ref([])

onMounted(async () => {
  try {
    [summary.value, usageRows.value] = await Promise.all([api.usageSummary(), api.usageSessions()])
  } catch { /* usage telemetry may be disabled — the dashboard still works */ }
})

const waiting = computed(() => props.sessions.filter(s => s.questionPending))
const running = computed(() => props.sessions.filter(s => ['Running', 'Pending'].includes(s.phase)))
const listed = computed(() => props.sessions.filter(s => !s.questionPending).slice(0, 6))
const paused = computed(() => props.sessions.filter(s => s.phase === 'Paused'))
const headline = computed(() => {
  const n = waiting.value.length
  if (n === 1) return 'One agent wants a word with you.'
  if (n > 1) return `${n} agents want a word with you.`
  if (running.value.length) return 'All quiet — your agents are working.'
  return 'Give an agent a task.'
})
const subline = computed(() => {
  const parts = []
  parts.push(`${running.value.length} running`)
  if (paused.value.length) parts.push(`${paused.value.length} paused`)
  if (summary.value) parts.push(`${formatCost(summary.value.costUsd)} spent so far`)
  return parts.join(' · ')
})
const topSpend = computed(() => {
  const rows = [...usageRows.value].sort((a, b) => (b.costUsd || 0) - (a.costUsd || 0)).slice(0, 3)
  const max = rows[0]?.costUsd || 0
  return rows.map(r => ({ ...r, pct: max ? Math.round(((r.costUsd || 0) / max) * 100) : 0 }))
})
function repoLabel(s) { return repoShortName(s.repoUrl || s.repos?.[0]?.url || '') }
</script>
<template>
  <div class="home">
    <div>
      <h2 class="headline">{{ headline }}</h2>
      <div class="subline">{{ subline }}</div>
    </div>
    <div class="grid">
      <div v-if="waiting.length" class="needs-you">
        <div class="ny-head"><span class="dot"></span>NEEDS YOU</div>
        <div v-for="s in waiting" :key="s.id" class="ny-card">
          <div class="ny-title">{{ s.title }}</div>
          <div class="ny-sub">The agent is waiting for your reply<template v-if="repoLabel(s)"> · <code>{{ repoLabel(s) }}</code></template></div>
          <div class="ny-actions"><button class="primary sm" @click="$emit('select', s.id)">Reply</button></div>
        </div>
      </div>
      <div v-else class="needs-you idle">
        <div class="ny-head calm"><span class="dot calm"></span>NOTHING NEEDS YOU</div>
        <p class="idle-text">No agent is waiting for a reply. Enjoy the silence — or hand out the next task.</p>
        <button class="sm" @click="$emit('new')">+ Give an agent a task</button>
      </div>
      <div class="stats-col">
        <div class="stat-row">
          <div class="stat card"><div class="stat-label">Running now</div><div class="stat-value">{{ running.length }}</div></div>
          <div class="stat card click" @click="$emit('usage')"><div class="stat-label">Total cost</div><div class="stat-value">{{ summary ? formatCost(summary.costUsd) : '—' }}</div></div>
        </div>
        <div class="card spend click" @click="$emit('usage')">
          <div class="spend-head"><span>Top spend by session</span><span class="more">Usage →</span></div>
          <template v-if="topSpend.length">
            <div v-for="r in topSpend" :key="r.sessionId" class="spend-row">
              <div class="spend-line"><span class="spend-title">{{ r.title || r.sessionId }}</span><span class="spend-cost">{{ formatCost(r.costUsd) }}</span></div>
              <div class="spend-bar"><div class="spend-fill" :style="{ width: r.pct + '%' }"></div></div>
            </div>
          </template>
          <p v-else class="idle-text">No usage recorded yet — data appears once agents report telemetry.</p>
        </div>
      </div>
    </div>
    <div class="card list">
      <div class="list-head"><span>What your agents are up to</span><button class="ghost more" @click="$emit('sessions')">All sessions →</button></div>
      <div v-for="s in listed" :key="s.id" class="row" @click="$emit('select', s.id)">
        <span class="pill" :style="{ color: statusStyle(s).color, background: statusStyle(s).bg }">{{ sessionStatus(s) }}</span>
        <span class="row-title">{{ s.title }} <code v-if="repoLabel(s)" class="row-repo">— {{ repoLabel(s) }}</code></span>
        <span v-if="s.schedule" class="row-meta">{{ s.schedule }}</span>
        <button v-if="s.canResume" class="ghost resume" @click.stop="$emit('resume', s.id)">Resume</button>
      </div>
      <div v-if="!listed.length" class="row none">No sessions yet — start one with “Give an agent a task”.</div>
    </div>
  </div>
</template>
<style scoped>
.home { flex: 1; min-width: 0; padding: 26px 28px; display: flex; flex-direction: column; gap: 20px; overflow-y: auto; }
.headline { font-size: 30px; font-weight: 700; margin: 0; }
.subline { font-size: 14px; color: var(--muted-2); margin-top: 5px; }
.grid { display: flex; gap: 14px; flex-wrap: wrap; }
.needs-you { flex: 1.2; min-width: 340px; background: #1f1b12; border: 1px solid #4a3e1e; border-radius: var(--radius-lg); padding: 16px 18px; display: flex; flex-direction: column; gap: 11px; }
.needs-you.idle { background: var(--panel); border-color: var(--border-2); align-items: flex-start; }
.ny-head { display: flex; align-items: center; gap: 8px; font-weight: 700; color: var(--warn); font-size: 13px; }
.ny-head.calm { color: var(--running); }
.dot { width: 8px; height: 8px; border-radius: 50%; background: var(--warn); }
.dot.calm { background: var(--running); }
.idle-text { color: var(--muted); font-size: 13px; margin: 0; }
.ny-card { background: #161511; border: 1px solid var(--border-2); border-radius: 12px; padding: 12px 14px; }
.ny-title { font-weight: 600; }
.ny-sub { font-size: 13px; color: var(--muted); margin-top: 3px; }
.ny-sub code { font-family: var(--mono); font-size: 12px; }
.ny-actions { margin-top: 10px; }
.sm { font-size: 12px; padding: 6px 13px; border-radius: 9px; }
.stats-col { flex: 1; min-width: 300px; display: flex; flex-direction: column; gap: 14px; }
.stat-row { display: grid; grid-template-columns: 1fr 1fr; gap: 14px; }
.stat { padding: 15px 17px; }
.stat-label { font-size: 12px; color: var(--muted-2); }
.stat-value { font-family: var(--display); font-size: 30px; font-weight: 700; margin-top: 5px; color: var(--strong); }
.click { cursor: pointer; }
.click:hover { border-color: var(--border-3); }
.spend { padding: 15px 17px; flex: 1; }
.spend-head { display: flex; justify-content: space-between; font-weight: 600; font-size: 13px; margin-bottom: 10px; }
.more { color: var(--muted-3); font-weight: 500; font-size: 12px; }
.more:hover { color: var(--accent); background: none; }
.spend-row { margin-bottom: 8px; }
.spend-line { display: flex; justify-content: space-between; font-size: 12px; margin-bottom: 4px; }
.spend-title { color: #d6d1c8; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; min-width: 0; }
.spend-cost { font-family: var(--mono); color: var(--muted); flex-shrink: 0; margin-left: 10px; }
.spend-bar { height: 4px; background: var(--border); border-radius: 2px; }
.spend-fill { height: 4px; background: var(--accent); border-radius: 2px; }
.list { overflow: hidden; }
.list-head { padding: 13px 18px; font-weight: 700; font-size: 14px; display: flex; justify-content: space-between; align-items: center; color: var(--strong); }
.row { padding: 11px 18px; display: flex; align-items: center; gap: 12px; border-top: 1px solid var(--border); cursor: pointer; }
.row:hover { background: var(--hover); }
.row .pill { width: 78px; text-align: center; flex-shrink: 0; }
.row-title { flex: 1; font-weight: 500; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.row-repo { font-family: var(--mono); font-size: 12px; color: var(--muted-3); }
.row-meta { font-family: var(--mono); font-size: 12px; color: var(--muted-3); flex-shrink: 0; }
.resume { color: var(--accent); font-weight: 700; font-size: 13px; }
.none { color: var(--muted); cursor: default; }
.none:hover { background: none; }
</style>
