<script setup>
import { computed, onMounted, ref } from 'vue'
import { api } from '../api.js'
import { formatCost, formatTokens, formatTokensExact, percent, totalTokens } from '../lib/usage.js'

const summary = ref(null)
const rows = ref([])
const loading = ref(true)
const error = ref('')

// The four token buckets, shared by the legend and every stacked bar.
const buckets = [
  { key: 'inputTokens', label: 'Input', color: 'var(--accent)' },
  { key: 'outputTokens', label: 'Output', color: 'var(--ok)' },
  { key: 'cacheReadTokens', label: 'Cache read', color: 'var(--sched)' },
  { key: 'cacheCreationTokens', label: 'Cache create', color: 'var(--muted-3)' }
]

async function load() {
  loading.value = true; error.value = ''
  try {
    const [s, list] = await Promise.all([api.usageSummary(), api.usageSessions()])
    summary.value = s
    rows.value = list
  } catch (e) { error.value = String(e.message || e) }
  finally { loading.value = false }
}
onMounted(load)

const summaryTotal = computed(() => totalTokens(summary.value))
const avgCost = computed(() => {
  const n = summary.value?.sessionCount || 0
  return n ? (summary.value.costUsd || 0) / n : 0
})
const sorted = computed(() => [...rows.value].sort((a, b) => (b.costUsd || 0) - (a.costUsd || 0)))

function segments(row) {
  const total = totalTokens(row)
  return buckets
    .map(b => ({ ...b, value: Number(row?.[b.key]) || 0, pct: percent(row?.[b.key], total) }))
    .filter(s => s.value > 0)
}
</script>
<template>
  <div class="usage-page">
    <div>
      <h2>Usage &amp; cost</h2>
      <div class="sub">Fed live from agent OpenTelemetry metrics.</div>
    </div>
    <p v-if="error" class="err">{{ error }}</p>
    <p v-if="loading" class="muted">Loading…</p>
    <template v-else>
      <div class="stats">
        <div class="card stat"><div class="stat-label">Total cost</div><div class="stat-value">{{ formatCost(summary?.costUsd) }}</div><div class="stat-sub">{{ summary?.sessionCount ?? 0 }} sessions with usage</div></div>
        <div class="card stat"><div class="stat-label">Tokens</div><div class="stat-value">{{ formatTokens(summaryTotal) }}</div><div class="stat-sub">{{ formatTokens(summary?.inputTokens) }} in · {{ formatTokens(summary?.outputTokens) }} out</div></div>
        <div class="card stat"><div class="stat-label">Avg per session</div><div class="stat-value">{{ formatCost(avgCost) }}</div><div class="stat-sub">cache {{ formatTokens((summary?.cacheReadTokens || 0) + (summary?.cacheCreationTokens || 0)) }}</div></div>
      </div>
      <div v-if="summaryTotal > 0" class="card blend">
        <div class="blend-head">Token mix</div>
        <div class="bar">
          <div v-for="s in segments(summary)" :key="s.key" class="seg" :style="{ width: s.pct + '%', background: s.color }" :title="`${s.label}: ${formatTokensExact(s.value)}`"></div>
        </div>
        <div class="legend">
          <span v-for="b in buckets" :key="b.key" class="legend-item"><span class="ldot" :style="{ background: b.color }"></span>{{ b.label }} <span class="lnum">{{ formatTokens(summary?.[b.key]) }}</span></span>
        </div>
      </div>
      <div class="card table">
        <div class="table-head">Cost by session</div>
        <div class="thead"><span>SESSION</span><span>TOKENS</span><span>MIX</span><span class="right">COST</span></div>
        <div v-for="r in sorted" :key="r.sessionId" class="trow">
          <span class="ttitle">{{ r.title || r.sessionId }}</span>
          <span class="tnum">{{ formatTokens(totalTokens(r)) }}</span>
          <span class="tbar"><span class="bar small"><span v-for="s in segments(r)" :key="s.key" class="seg" :style="{ width: s.pct + '%', background: s.color }"></span></span></span>
          <span class="tnum right">{{ formatCost(r.costUsd) }}</span>
        </div>
        <p v-if="!sorted.length" class="muted pad">No usage recorded yet. Enable <code>telemetry.enabled</code> on the server for this to populate.</p>
      </div>
    </template>
  </div>
</template>
<style scoped>
.usage-page { flex: 1; min-width: 0; padding: 26px 28px; display: flex; flex-direction: column; gap: 18px; overflow-y: auto; }
h2 { font-size: 28px; font-weight: 700; margin: 0; }
.sub { font-size: 14px; color: var(--muted-2); margin-top: 4px; }
.stats { display: grid; grid-template-columns: repeat(3, 1fr); gap: 14px; }
.stat { padding: 15px 17px; }
.stat-label { font-size: 12px; color: var(--muted-2); }
.stat-value { font-family: var(--display); font-size: 30px; font-weight: 700; margin-top: 5px; color: var(--strong); }
.stat-sub { font-size: 12px; color: var(--muted-3); margin-top: 3px; }
.blend { padding: 17px 20px; }
.blend-head { font-weight: 600; font-size: 13px; margin-bottom: 12px; }
.bar { display: flex; height: 14px; border-radius: 7px; overflow: hidden; background: var(--input); border: 1px solid var(--border-2); }
.bar.small { height: 8px; border-radius: 4px; display: flex; flex: 1; }
.seg { height: 100%; min-width: 2px; display: inline-block; }
.legend { display: flex; flex-wrap: wrap; gap: 14px; margin-top: 10px; font-size: 12px; }
.legend-item { display: inline-flex; align-items: center; gap: 6px; }
.ldot { width: 9px; height: 9px; border-radius: 2px; display: inline-block; }
.lnum { font-family: var(--mono); color: var(--muted); }
.table { overflow: hidden; }
.table-head { padding: 13px 18px; font-weight: 700; font-size: 14px; color: var(--strong); }
.thead { display: grid; grid-template-columns: 2.2fr 0.8fr 1.4fr 0.8fr; padding: 8px 18px; font-size: 11px; font-weight: 700; letter-spacing: 0.08em; color: var(--muted-3); border-top: 1px solid var(--border); }
.trow { display: grid; grid-template-columns: 2.2fr 0.8fr 1.4fr 0.8fr; padding: 11px 18px; border-top: 1px solid var(--border); align-items: center; }
.trow:hover { background: var(--hover); }
.ttitle { font-weight: 500; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; padding-right: 10px; }
.tnum { font-family: var(--mono); font-size: 12px; color: var(--muted); }
.tbar { display: flex; padding-right: 14px; }
.right { text-align: right; }
.muted { color: var(--muted); font-size: 13px; }
.pad { padding: 14px 18px; }
.err { color: var(--danger); font: 12px var(--mono); }
code { font-family: var(--mono); font-size: 12px; }
@media (max-width: 700px) { .stats { grid-template-columns: 1fr; } .thead, .trow { grid-template-columns: 2fr 1fr 1fr; } .tbar { display: none; } }
</style>
