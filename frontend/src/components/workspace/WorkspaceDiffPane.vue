<script setup>
import { onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { api, getSharedWorkspaceDiff } from '../../api.js'
const props = defineProps({ sessionId: { type: String, required: true }, sharedToken: { type: String, default: null }, active: { type: Boolean, default: true } })
const comparison = ref('base'), result = ref(null), error = ref(''), loading = ref(false)
let timer, generation = 0, disposed = false
async function refresh() {
  clearTimeout(timer)
  if (disposed || !props.active || document.hidden) return
  const current = ++generation
  loading.value = true
  try {
    const next = props.sharedToken ? await getSharedWorkspaceDiff(props.sharedToken, comparison.value) : await api.workspaceDiff(props.sessionId, comparison.value)
    if (current !== generation || disposed) return
    result.value = next; error.value = ''
  } catch {
    if (current !== generation || disposed) return
    result.value = null; error.value = 'Changes are unavailable. The session must be running with a compatible runtime.'
  } finally {
    if (current === generation && !disposed) { loading.value = false; timer = setTimeout(refresh, 3000) }
  }
}
function restart() { generation++; result.value = null; clearTimeout(timer); void refresh() }
function visibilityChanged() { if (document.hidden) { generation++; clearTimeout(timer) } else restart() }
onMounted(() => { document.addEventListener('visibilitychange', visibilityChanged); void refresh() })
watch(() => [props.sessionId, props.sharedToken, props.active, comparison.value], restart)
onBeforeUnmount(() => { disposed = true; generation++; clearTimeout(timer); document.removeEventListener('visibilitychange', visibilityChanged) })
const omission = { binary: 'Binary file', large: 'Large file — patch omitted', limit: 'Response limit — patch omitted', symlink: 'Symbolic link — patch omitted', unavailable: 'File changed or could not be read' }
function lineClass(line) {
  if (line.startsWith('+++') || line.startsWith('---')) return 'metadata'
  if (line.startsWith('+')) return 'addition'
  if (line.startsWith('-')) return 'deletion'
  if (line.startsWith('@@')) return 'hunk'
  return ''
}
</script>
<template>
  <section class="diff-pane" data-workspace-diff aria-label="Code changes">
    <div class="diff-toolbar">
      <label>Compare with <select v-model="comparison" aria-label="Diff comparison"><option value="base">Branch base</option><option value="head">Current commit (HEAD)</option></select></label>
      <span v-if="loading" role="status">Refreshing…</span>
    </div>
    <p v-if="error" role="status">{{ error }}</p>
    <template v-if="result">
      <p v-if="result.truncated" role="status">Some changes were omitted because the review limit was reached.</p>
      <p v-if="!result.repositories.length">No repositories found in this session.</p>
      <section v-for="repo in result.repositories" :key="repo.path">
        <h3>{{ repo.path }} <small v-if="repo.base">{{ repo.base.slice(0, 12) }}</small></h3>
        <p v-if="repo.error">Repository could not be read.</p><p v-else-if="!repo.files.length">No changes.</p>
        <details v-for="file in repo.files" :key="file.path" open>
          <summary>{{ file.path }}</summary><p v-if="file.omitted">{{ omission[file.omitted] || 'Patch omitted' }}</p>
          <pre v-else aria-label="Unified diff"><code><span v-for="(line, index) in file.patch.split('\n')" :key="index" :class="lineClass(line)">{{ line }}{{ '\n' }}</span></code></pre>
        </details>
      </section>
    </template>
  </section>
</template>
<style scoped>
.diff-pane { height: 100%; overflow: auto; color: var(--text, #d6dde7); background: var(--bg, #111721); font-size: 12px; }
.diff-toolbar { position: sticky; top: 0; padding: 10px; background: var(--panel, #171c24); display: flex; flex-wrap: wrap; gap: 8px; }
select { max-width: 100%; color: inherit; background: var(--panel, #171c24); }
p, h3 { margin: 10px; overflow-wrap: anywhere; } small { font-weight: normal; color: var(--muted-3); }
summary { padding: 8px; cursor: pointer; overflow-wrap: anywhere; border-top: 1px solid var(--border, #29303a); }
pre { overflow-x: auto; margin: 0; padding: 8px; font: 11px/1.5 var(--mono, monospace); tab-size: 4; }
.addition { color: #9be9ad; background: #173725; } .deletion { color: #ffb1b1; background: #412328; } .hunk { color: #98c9ff; } .metadata { color: #b8c2cf; }
</style>
