<script setup>
import { ref, onMounted, watch } from 'vue'
import { api, config } from '../api.js'

// v-model: array of { url, branch, providerId }
const props = defineProps({ modelValue: { type: Array, default: () => [] } })
const emit = defineEmits(['update:modelValue'])

const repos = ref([...props.modelValue])
watch(repos, v => emit('update:modelValue', v), { deep: true })

const providers = ref([])          // connected git providers
const provider = ref('')           // selected provider id for search
const query = ref('')
const results = ref([])
const searching = ref(false)
const error = ref('')
const manualUrl = ref('')
const manualBranch = ref('')
let debounce

onMounted(async () => {
  if (!config.gitEnabled) return
  try {
    providers.value = (await api.gitProviders()).filter(p => p.connected)
    if (providers.value.length) { provider.value = providers.value[0].id; search() }
  } catch (e) { /* ignore — manual entry still works */ }
})

watch([query, provider], () => {
  clearTimeout(debounce)
  debounce = setTimeout(search, 250)
})

async function search() {
  if (!provider.value) return
  searching.value = true; error.value = ''
  try { results.value = await api.gitProjects(provider.value, query.value) }
  catch (e) { error.value = String(e.message || e); results.value = [] }
  finally { searching.value = false }
}

function isSelected(url) { return repos.value.some(r => r.url === url) }
function toggle(p) {
  const i = repos.value.findIndex(r => r.url === p.url)
  if (i >= 0) repos.value.splice(i, 1)
  else repos.value.push({ url: p.url, branch: p.defaultBranch || null, providerId: p.providerId })
}
function removeRepo(i) { repos.value.splice(i, 1) }
function addManual() {
  const url = manualUrl.value.trim()
  if (!url || isSelected(url)) { manualUrl.value = ''; return }
  repos.value.push({ url, branch: manualBranch.value.trim() || null, providerId: null })
  manualUrl.value = ''; manualBranch.value = ''
}
</script>

<template>
  <div class="picker">
    <!-- selected repos -->
    <div v-if="repos.length" class="chips">
      <span v-for="(r, i) in repos" :key="r.url + i" class="chip">
        {{ r.url.replace(/^https?:\/\/[^/]+\//, '').replace(/\.git$/, '') }}<span v-if="r.branch" class="br">@{{ r.branch }}</span>
        <button class="x" @click="removeRepo(i)">✕</button>
      </span>
    </div>

    <!-- search a connected provider -->
    <div v-if="providers.length" class="search">
      <select v-if="providers.length > 1" v-model="provider">
        <option v-for="p in providers" :key="p.id" :value="p.id">{{ p.displayName }}</option>
      </select>
      <input v-model="query" :placeholder="`Search ${providers.find(p=>p.id===provider)?.displayName || 'repositories'}…`" />
    </div>
    <div v-if="providers.length" class="results">
      <p v-if="searching" class="muted">Searching…</p>
      <p v-else-if="error" class="err">{{ error }}</p>
      <p v-else-if="!results.length" class="muted">No matching repositories.</p>
      <label v-for="p in results" :key="p.url" class="result">
        <input type="checkbox" :checked="isSelected(p.url)" @change="toggle(p)" />
        <span class="full">{{ p.fullName }}</span>
        <span v-if="p.defaultBranch" class="db">{{ p.defaultBranch }}</span>
      </label>
    </div>
    <p v-else-if="config.gitEnabled" class="muted">
      No connected Git account — connect one under Account, or add a repo URL manually below.
    </p>

    <!-- manual URL (fallback / SSH / anonymous) -->
    <div class="manual">
      <input v-model="manualUrl" placeholder="…or paste a clone URL (git@… or https://…)" @keyup.enter="addManual" />
      <input v-model="manualBranch" class="branch" placeholder="branch" @keyup.enter="addManual" />
      <button @click="addManual">Add</button>
    </div>
  </div>
</template>

<style scoped>
.picker { display: flex; flex-direction: column; gap: 10px; }
.chips { display: flex; flex-wrap: wrap; gap: 6px; }
.chip { display: inline-flex; align-items: center; gap: 6px; font-family: var(--mono); font-size: 12px; background: var(--panel-2); border: 1px solid var(--border); border-radius: 999px; padding: 3px 10px; }
.chip .br { color: var(--muted); }
.chip .x { background: none; border: none; color: var(--muted); padding: 0; font-size: 12px; }
.chip .x:hover { color: var(--danger); border: none; }
.search { display: flex; gap: 8px; }
.search select { flex: none; width: auto; }
.results { max-height: 180px; overflow-y: auto; border: 1px solid var(--border); border-radius: 8px; padding: 4px; }
.result { display: flex; align-items: center; gap: 8px; padding: 6px 8px; font-size: 13px; cursor: pointer; border-radius: 6px; }
.result:hover { background: var(--panel-2); }
.result input { width: auto; }
.result .full { flex: 1; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.result .db { font-family: var(--mono); font-size: 10px; color: var(--muted); }
.manual { display: flex; gap: 8px; }
.manual .branch { flex: none; width: 110px; }
.muted { color: var(--muted); font-size: 12px; margin: 4px 0; }
.err { color: var(--danger); font-family: var(--mono); font-size: 12px; }
</style>
