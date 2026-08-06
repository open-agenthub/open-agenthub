<script setup>
import { computed, onMounted, ref } from 'vue'
import { api } from '../api.js'
import { formatApproxCost, formatBytes, formatCost, formatCpuSeconds, formatTokens, formatTokensExact, hasResources, percent, sessionCost, totalTokens } from '../lib/usage.js'

const summary = ref(null)
const rows = ref([])
const limit = ref(null)
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
    const [s, list, l] = await Promise.all([api.usageSummary(), api.usageSessions(), api.usageLimit()])
    summary.value = s
    rows.value = list
    limit.value = l
  } catch (e) { error.value = String(e.message || e) }
  finally { loading.value = false }
}
onMounted(load)

const summaryTotal = computed(() => totalTokens(summary.value))
const apiCost = computed(() => summary.value?.apiCostUsd ?? summary.value?.costUsd ?? 0)
const subValue = computed(() => summary.value?.subscriptionEstimatedCostUsd ?? 0)
const sorted = computed(() =>
  [...rows.value].sort((a, b) => sessionCost(b).usd - sessionCost(a).usd))

function segments(row) {
  const total = totalTokens(row)
  return buckets
    .map(b => ({ ...b, value: Number(row?.[b.key]) || 0, pct: percent(row?.[b.key], total) }))
    .filter(s => s.value > 0)
}

// ---- Personal monthly API budget (community feature) ----
const limitInput = ref('')
const savingLimit = ref(false)
const limitError = ref('')
const budgetPct = computed(() => {
  if (!limit.value?.effectiveLimitUsd) return 0
  return Math.min(100, percent(limit.value.monthApiCostUsd, limit.value.effectiveLimitUsd))
})
const adminLimited = computed(() =>
  limit.value?.source && limit.value.source !== 'personal')

async function saveLimit() {
  limitError.value = ''
  const raw = limitInput.value.trim()
  const parsed = raw === '' ? null : Number(raw)
  if (parsed !== null && (!Number.isFinite(parsed) || parsed < 0)) {
    limitError.value = 'Enter a non-negative amount (or leave empty for no limit).'
    return
  }
  savingLimit.value = true
  try { limit.value = await api.setUsageLimit(parsed) }
  catch (e) { limitError.value = String(e.message || e) }
  finally { savingLimit.value = false }
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
        <div class="card stat" data-api-cost>
          <div class="stat-label">API cost</div>
          <div class="stat-value">{{ formatCost(apiCost) }}</div>
          <div class="stat-sub">billed against API keys · {{ summary?.sessionCount ?? 0 }} sessions with usage</div>
        </div>
        <div class="card stat" data-subscription-value>
          <div class="stat-label">Covered by subscription</div>
          <div class="stat-value approx">{{ formatApproxCost(subValue) }}</div>
          <div class="stat-sub">what these tokens would have cost at API prices</div>
        </div>
        <div class="card stat">
          <div class="stat-label">Tokens</div>
          <div class="stat-value">{{ formatTokens(summaryTotal) }}</div>
          <div class="stat-sub">{{ formatTokens(summary?.inputTokens) }} in · {{ formatTokens(summary?.outputTokens) }} out · cache {{ formatTokens((summary?.cacheReadTokens || 0) + (summary?.cacheCreationTokens || 0)) }}</div>
        </div>
        <div class="card stat" data-compute>
          <div class="stat-label">Compute</div>
          <div class="stat-value">{{ formatCpuSeconds(summary?.cpuSeconds) }}</div>
          <div class="stat-sub">CPU time · network ↓{{ formatBytes(summary?.rxBytes) }} ↑{{ formatBytes(summary?.txBytes) }}</div>
        </div>
      </div>
      <div class="card budget" data-usage-limit>
        <div class="budget-head">
          <span>Monthly API budget</span>
          <span class="budget-month">{{ formatCost(limit?.monthApiCostUsd) }} spent this month</span>
        </div>
        <template v-if="limit?.effectiveLimitUsd">
          <div class="bar budget-bar" :class="{ full: limit.blocked }">
            <div class="seg" :style="{ width: budgetPct + '%', background: limit.blocked ? 'var(--danger)' : 'var(--accent)' }"></div>
          </div>
          <div class="budget-meta">
            <span>{{ formatCost(limit.monthApiCostUsd) }} of {{ formatCost(limit.effectiveLimitUsd) }}<template v-if="adminLimited"> · set by your admin ({{ limit.source }})</template></span>
            <span v-if="limit.blocked" class="blocked">New API-billed sessions are blocked — subscription sessions still work.</span>
          </div>
        </template>
        <p v-else class="muted nolimit">No budget set — API-billed sessions are unlimited.</p>
        <div class="budget-edit">
          <label>My limit (USD/month)</label>
          <input v-model="limitInput" :placeholder="limit?.personalLimitUsd != null ? String(limit.personalLimitUsd) : 'none'" inputmode="decimal" />
          <button class="sm" data-save-limit :disabled="savingLimit" @click="saveLimit">{{ savingLimit ? 'Saving…' : 'Save' }}</button>
          <span class="hint">Empty = remove my limit. Applies to API-key usage only.</span>
        </div>
        <p v-if="limitError" class="err">{{ limitError }}</p>
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
        <div class="thead"><span>SESSION</span><span>TOKENS</span><span>MIX</span><span>RESOURCES</span><span class="right">COST</span></div>
        <div v-for="r in sorted" :key="r.sessionId" class="trow">
          <span class="ttitle">{{ r.title || r.sessionId }}</span>
          <span class="tnum">{{ formatTokens(totalTokens(r)) }}</span>
          <span class="tbar"><span class="bar small"><span v-for="s in segments(r)" :key="s.key" class="seg" :style="{ width: s.pct + '%', background: s.color }"></span></span></span>
          <span class="tnum tres" :title="hasResources(r) ? `CPU ${formatCpuSeconds(r.cpuSeconds)} · peak memory ${formatBytes(r.peakMemoryBytes)} · received ${formatBytes(r.rxBytes)} · sent ${formatBytes(r.txBytes)}` : ''">
            <template v-if="hasResources(r)">{{ formatCpuSeconds(r.cpuSeconds) }} · {{ formatBytes(r.peakMemoryBytes) }} · ↓{{ formatBytes(r.rxBytes) }} ↑{{ formatBytes(r.txBytes) }}</template>
            <template v-else>—</template>
          </span>
          <span class="tnum right">
            <span v-if="sessionCost(r).approx" class="src-pill sub" title="Covered by a Claude subscription — estimated API-equivalent">sub</span>
            <span v-else class="src-pill api" title="Billed against an API key">api</span>
            {{ sessionCost(r).approx ? formatApproxCost(sessionCost(r).usd) : formatCost(sessionCost(r).usd) }}
          </span>
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
.stats { display: grid; grid-template-columns: repeat(4, 1fr); gap: 14px; }
.stat { padding: 15px 17px; }
.stat-label { font-size: 12px; color: var(--muted-2); }
.stat-value { font-family: var(--display); font-size: 30px; font-weight: 700; margin-top: 5px; color: var(--strong); }
.stat-value.approx { color: var(--sched); }
.stat-sub { font-size: 12px; color: var(--muted-3); margin-top: 3px; }
.budget { padding: 15px 17px; display: flex; flex-direction: column; gap: 10px; }
.budget-head { display: flex; justify-content: space-between; font-weight: 600; font-size: 13px; }
.budget-month { color: var(--muted-2); font-weight: 500; font-size: 12px; }
.budget-bar { height: 10px; border-radius: 5px; }
.budget-meta { display: flex; justify-content: space-between; gap: 12px; font-size: 12px; color: var(--muted); flex-wrap: wrap; }
.blocked { color: var(--danger); font-weight: 600; }
.nolimit { margin: 0; }
.budget-edit { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; }
.budget-edit label { font-size: 12px; color: var(--muted-2); }
.budget-edit input { width: 110px; }
.budget-edit .hint { font-size: 11px; color: var(--muted-3); }
.sm { font-size: 12px; padding: 6px 13px; border-radius: 9px; }
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
.thead { display: grid; grid-template-columns: 1.8fr 0.7fr 1fr 1.6fr 1fr; padding: 8px 18px; font-size: 11px; font-weight: 700; letter-spacing: 0.08em; color: var(--muted-3); border-top: 1px solid var(--border); }
.trow { display: grid; grid-template-columns: 1.8fr 0.7fr 1fr 1.6fr 1fr; padding: 11px 18px; border-top: 1px solid var(--border); align-items: center; }
.trow:hover { background: var(--hover); }
.ttitle { font-weight: 500; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; padding-right: 10px; }
.tnum { font-family: var(--mono); font-size: 12px; color: var(--muted); }
.tbar { display: flex; padding-right: 14px; }
.tres { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; padding-right: 10px; }
.right { text-align: right; }
.src-pill { display: inline-block; font-size: 10px; font-weight: 700; letter-spacing: 0.06em; padding: 1px 7px; border-radius: 8px; margin-right: 6px; vertical-align: 1px; }
.src-pill.sub { color: var(--sched); background: rgba(201, 184, 249, 0.12); }
.src-pill.api { color: var(--ok); background: rgba(95, 214, 139, 0.12); }
.muted { color: var(--muted); font-size: 13px; }
.pad { padding: 14px 18px; }
.err { color: var(--danger); font: 12px var(--mono); }
code { font-family: var(--mono); font-size: 12px; }
@media (max-width: 700px) { .stats { grid-template-columns: 1fr; } .thead, .trow { grid-template-columns: 2fr 1fr 1fr; } .tbar, .tres { display: none; } }
@media (max-width: 1100px) and (min-width: 701px) { .stats { grid-template-columns: repeat(2, 1fr); } }
</style>
