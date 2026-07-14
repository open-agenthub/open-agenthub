<script setup>
import { ref, computed, onMounted } from 'vue'
import { api } from '../api.js'
import { formatTokens, formatTokensExact, formatCost, totalTokens, percent } from '../lib/usage.js'

const emit = defineEmits(['close'])
const props = defineProps({ embedded: { type: Boolean, default: false } })

const summary = ref(null)
const sessions = ref([])
const loading = ref(true)
const error = ref('')

// The four token buckets, shared by the legend and every stacked bar.
const buckets = [
  { key: 'inputTokens', label: 'Input', color: 'var(--accent)' },
  { key: 'outputTokens', label: 'Output', color: 'var(--ok)' },
  { key: 'cacheReadTokens', label: 'Cache read', color: 'var(--running)' },
  { key: 'cacheCreationTokens', label: 'Cache create', color: 'var(--muted)' }
]

async function load() {
  loading.value = true; error.value = ''
  try {
    const [s, list] = await Promise.all([api.usageSummary(), api.usageSessions()])
    summary.value = s
    sessions.value = list
  } catch (e) { error.value = String(e.message || e) }
  finally { loading.value = false }
}

onMounted(load)

const summaryTotal = computed(() => totalTokens(summary.value))

// Stacked-bar segments (skip zero-width slices so borders stay clean).
function segments(row) {
  const total = totalTokens(row)
  return buckets
    .map(b => ({ ...b, value: Number(row?.[b.key]) || 0, pct: percent(row?.[b.key], total) }))
    .filter(s => s.value > 0)
}

function fmtDate(d) {
  if (!d) return '—'
  try { return new Date(d).toLocaleString() } catch { return d }
}
</script>

<template>
  <div :class="embedded ? 'embed' : 'overlay'" @click.self="embedded || $emit('close')">
    <div :class="embedded ? 'embed-inner' : 'modal'">
      <h3>Usage</h3>
      <p class="note">
        Token and cost usage across your sessions, collected from the agents via OpenTelemetry.
        Enable <code>telemetry.enabled</code> on the server for this to populate.
      </p>

      <p v-if="error" class="err">{{ error }}</p>
      <p v-if="loading" class="muted">Loading…</p>

      <template v-else>
        <div class="tiles">
          <div class="tile">
            <span class="tile-label">Total tokens</span>
            <span class="tile-value">{{ formatTokens(summaryTotal) }}</span>
            <span class="tile-sub">{{ formatTokensExact(summaryTotal) }}</span>
          </div>
          <div class="tile">
            <span class="tile-label">Total cost</span>
            <span class="tile-value">{{ formatCost(summary?.costUsd) }}</span>
            <span class="tile-sub">USD</span>
          </div>
          <div class="tile">
            <span class="tile-label">Sessions</span>
            <span class="tile-value">{{ summary?.sessionCount ?? 0 }}</span>
            <span class="tile-sub">with recorded usage</span>
          </div>
          <div class="tile">
            <span class="tile-label">Input / Output</span>
            <span class="tile-value">{{ formatTokens(summary?.inputTokens) }} / {{ formatTokens(summary?.outputTokens) }}</span>
            <span class="tile-sub">cache {{ formatTokens((summary?.cacheReadTokens || 0) + (summary?.cacheCreationTokens || 0)) }}</span>
          </div>
        </div>

        <div v-if="summaryTotal > 0" class="overall">
          <div class="bar">
            <div v-for="s in segments(summary)" :key="s.key" class="seg"
              :style="{ width: s.pct + '%', background: s.color }" :title="`${s.label}: ${formatTokensExact(s.value)}`"></div>
          </div>
          <div class="legend">
            <span v-for="b in buckets" :key="b.key" class="legend-item">
              <span class="dot" :style="{ background: b.color }"></span>{{ b.label }}
              <span class="muted">{{ formatTokens(summary?.[b.key]) }}</span>
            </span>
          </div>
        </div>

        <h4 class="sub-head">Per session</h4>
        <p v-if="!sessions.length" class="muted">No usage recorded yet.</p>
        <div v-else class="rows">
          <div v-for="row in sessions" :key="row.sessionId" class="urow">
            <div class="urow-head">
              <span class="urow-title">{{ row.title || row.sessionId }}</span>
              <span class="urow-nums">
                <span class="urow-tok">{{ formatTokens(totalTokens(row)) }} tok</span>
                <span class="urow-cost">{{ formatCost(row.costUsd) }}</span>
              </span>
            </div>
            <div class="bar small">
              <div v-for="s in segments(row)" :key="s.key" class="seg"
                :style="{ width: s.pct + '%', background: s.color }"
                :title="`${s.label}: ${formatTokensExact(s.value)}`"></div>
            </div>
            <span class="urow-meta">updated {{ fmtDate(row.updatedAt) }}</span>
          </div>
        </div>
      </template>

      <div class="row">
        <button @click="load" :disabled="loading">Refresh</button>
        <button v-if="!embedded" @click="$emit('close')">Close</button>
      </div>
    </div>
  </div>
</template>

<style scoped>
.overlay {
  position: fixed; inset: 0; background: rgba(5,7,10,.7);
  display: flex; align-items: flex-start; justify-content: center; padding: 24px; overflow-y: auto; z-index: 50;
}
.modal { width: 720px; max-width: 100%; background: var(--panel); border: 1px solid var(--border); border-radius: 14px; padding: 22px; }
.modal h3 { margin: 0 0 6px; }
.note { color: var(--muted); font-size: 12px; line-height: 1.5; margin: 0 0 16px; }
.note code { font-family: var(--mono); font-size: 11px; }
.tiles { display: grid; grid-template-columns: repeat(4, 1fr); gap: 10px; margin-bottom: 16px; }
.tile { background: var(--panel-2); border: 1px solid var(--border); border-radius: 10px; padding: 12px; display: flex; flex-direction: column; gap: 2px; min-width: 0; }
.tile-label { color: var(--muted); font-size: 11px; text-transform: uppercase; letter-spacing: .04em; }
.tile-value { font-size: 20px; font-weight: 700; font-family: var(--mono); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.tile-sub { color: var(--muted); font-size: 11px; font-family: var(--mono); }
.overall { margin-bottom: 18px; }
.bar { display: flex; height: 14px; border-radius: 7px; overflow: hidden; background: var(--panel-2); border: 1px solid var(--border); }
.bar.small { height: 8px; border-radius: 4px; }
.seg { height: 100%; min-width: 2px; }
.legend { display: flex; flex-wrap: wrap; gap: 14px; margin-top: 8px; font-size: 12px; }
.legend-item { display: inline-flex; align-items: center; gap: 6px; }
.legend-item .muted { font-family: var(--mono); }
.dot { width: 9px; height: 9px; border-radius: 2px; display: inline-block; }
.muted { color: var(--muted); font-size: 12px; }
.sub-head { margin: 6px 0 10px; font-size: 13px; }
.rows { display: flex; flex-direction: column; gap: 12px; margin-bottom: 16px; }
.urow { border: 1px solid var(--border); border-radius: 10px; padding: 10px 12px; }
.urow-head { display: flex; align-items: baseline; justify-content: space-between; gap: 12px; margin-bottom: 8px; }
.urow-title { font-weight: 600; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.urow-nums { display: flex; gap: 12px; flex-shrink: 0; font-family: var(--mono); font-size: 12px; }
.urow-cost { color: var(--ok); }
.urow-meta { display: block; margin-top: 6px; color: var(--muted); font-size: 11px; font-family: var(--mono); }
.row { display: flex; justify-content: flex-end; gap: 10px; margin-top: 8px; }
.err { color: var(--danger); font-family: var(--mono); font-size: 12px; }
@media (max-width: 620px) {
  .tiles { grid-template-columns: repeat(2, 1fr); }
}
</style>
