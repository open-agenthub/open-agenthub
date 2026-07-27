<script setup>
import { computed, onMounted, ref } from 'vue'
import { api } from '../api.js'
import { formatCost } from '../lib/usage.js'

defineProps({ embedded: { type: Boolean, default: false } })

const limits = ref([])
const groups = ref([])
const loading = ref(true)
const error = ref('')
const needsLicense = ref(false)

async function load() {
  loading.value = true; error.value = ''; needsLicense.value = false
  try {
    [limits.value, groups.value] = await Promise.all([api.eeListLimits(), api.eeListGroups()])
  } catch (e) {
    if (e.status === 402) needsLicense.value = true
    else error.value = String(e.message || e)
  } finally { loading.value = false }
}
onMounted(load)

const globalLimit = computed(() => limits.value.find(l => l.scope === 'global'))
const groupLimits = computed(() => limits.value.filter(l => l.scope === 'group'))
const userLimits = computed(() => limits.value.filter(l => l.scope === 'user'))

// ---- edit state ----
const globalInput = ref('')
const newScope = ref('user')
const newTarget = ref('')
const newAmount = ref('')
const busy = ref(false)

function parseAmount(raw) {
  const n = Number(String(raw).trim())
  return Number.isFinite(n) && n >= 0 ? n : null
}

async function run(action) {
  busy.value = true; error.value = ''
  try { limits.value = await action() ?? limits.value }
  catch (e) { error.value = String(e.message || e) }
  finally { busy.value = false }
}

async function saveGlobal() {
  const amount = parseAmount(globalInput.value)
  if (amount === null) { error.value = 'Enter a non-negative amount.'; return }
  await run(() => api.eeSetLimit({ scope: 'global', limitUsd: amount }))
  globalInput.value = ''
}

async function addLimit() {
  const amount = parseAmount(newAmount.value)
  if (amount === null) { error.value = 'Enter a non-negative amount.'; return }
  if (!newTarget.value.trim()) { error.value = 'Enter a user or group name.'; return }
  await run(() => api.eeSetLimit({ scope: newScope.value, target: newTarget.value.trim(), limitUsd: amount }))
  newTarget.value = ''; newAmount.value = ''
}

async function removeLimit(l) {
  await run(async () => { await api.eeDeleteLimit(l.scope, l.target); return limits.value.filter(x => !(x.scope === l.scope && x.target === l.target)) })
}

async function setRole(group, role) {
  busy.value = true; error.value = ''
  try { groups.value = await api.eeSetGroupRole(group, role || null) }
  catch (e) { error.value = String(e.message || e) }
  finally { busy.value = false }
}
</script>
<template>
  <div class="pane" :class="{ embed: embedded }">
    <h3>Usage limits &amp; groups</h3>
    <p class="lead">Monthly API budgets for everyone, a group, or a single user — the strictest limit wins, and personal limits still apply. Groups come from the OAuth token; map them to roles here.</p>
    <p v-if="error" class="err">{{ error }}</p>
    <p v-if="loading" class="muted">Loading…</p>
    <div v-else-if="needsLicense" class="card locked" data-limits-locked>
      <b>Enterprise feature.</b> Usage limits and group roles need an active enterprise license —
      activate one in the License tab.
    </div>
    <template v-else>
      <div class="card block" data-global-limit>
        <div class="block-head">Global limit</div>
        <div class="rowline">
          <span class="grow">{{ globalLimit ? `${formatCost(globalLimit.limitUsd)} per user and month` : 'No global limit — API usage is unlimited by default.' }}</span>
          <input v-model="globalInput" :placeholder="globalLimit ? String(globalLimit.limitUsd) : 'USD/month'" inputmode="decimal" />
          <button class="sm" :disabled="busy" data-save-global @click="saveGlobal">Set</button>
          <button v-if="globalLimit" class="sm ghost danger-text" :disabled="busy" @click="removeLimit(globalLimit)">Remove</button>
        </div>
      </div>

      <div class="card block" data-scoped-limits>
        <div class="block-head">Per-user and per-group limits</div>
        <div v-for="l in [...userLimits, ...groupLimits]" :key="l.scope + ':' + l.target" class="rowline bordered">
          <span class="pill" :class="l.scope">{{ l.scope }}</span>
          <span class="grow mono">{{ l.target }}</span>
          <span class="mono">{{ formatCost(l.limitUsd) }}/mo</span>
          <button class="sm ghost danger-text" :disabled="busy" @click="removeLimit(l)">Remove</button>
        </div>
        <p v-if="!userLimits.length && !groupLimits.length" class="muted">No user or group limits yet.</p>
        <div class="rowline add">
          <select v-model="newScope"><option value="user">user</option><option value="group">group</option></select>
          <input v-model="newTarget" class="grow" :placeholder="newScope === 'user' ? 'username' : 'group name'" data-limit-target />
          <input v-model="newAmount" placeholder="USD/month" inputmode="decimal" data-limit-amount />
          <button class="sm" :disabled="busy" data-add-limit @click="addLimit">Add</button>
        </div>
      </div>

      <div class="card block" data-group-roles>
        <div class="block-head">Groups &amp; roles</div>
        <p class="muted intro">Groups are read from the <code>groups</code> claim of the OAuth token at sign-in. Members of an <b>admin</b> group get access to this admin area.</p>
        <div v-for="g in groups" :key="g.name" class="rowline bordered" :data-group-row="g.name">
          <span class="grow mono">{{ g.name }}</span>
          <span class="members">{{ g.memberCount }} member{{ g.memberCount === 1 ? '' : 's' }}</span>
          <select :value="g.role || ''" :disabled="busy" @change="setRole(g.name, $event.target.value)">
            <option value="">no role</option>
            <option value="user">user</option>
            <option value="admin">admin</option>
          </select>
        </div>
        <p v-if="!groups.length" class="muted">No groups seen yet — they appear after users sign in with a token that carries a groups claim.</p>
      </div>
    </template>
  </div>
</template>
<style scoped>
.pane { max-width: 640px; padding: 26px 30px; display: flex; flex-direction: column; gap: 14px; }
.pane h3 { font-size: 22px; margin: 0; }
.lead { font-size: 13px; color: var(--muted-2); margin: 0 0 4px; }
.block { padding: 15px 17px; display: flex; flex-direction: column; gap: 10px; }
.block-head { font-weight: 700; font-size: 13px; color: var(--strong); }
.rowline { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; }
.rowline.bordered { border-top: 1px solid var(--border); padding-top: 10px; }
.rowline.add { border-top: 1px solid var(--border); padding-top: 12px; }
.grow { flex: 1; min-width: 120px; }
.mono { font-family: var(--mono); font-size: 12px; }
input, select { max-width: 140px; }
input.grow { max-width: none; }
.sm { font-size: 12px; padding: 6px 13px; border-radius: 9px; }
.danger-text { color: var(--danger); }
.pill { font-size: 10px; font-weight: 700; letter-spacing: 0.06em; padding: 1px 8px; border-radius: 8px; }
.pill.user { color: var(--accent); background: rgba(90, 169, 245, 0.12); }
.pill.group { color: var(--sched); background: rgba(201, 184, 249, 0.12); }
.members { font-size: 12px; color: var(--muted-3); }
.intro { margin: 0; font-size: 12px; }
.locked { padding: 16px 18px; font-size: 13px; color: var(--muted); }
.muted { color: var(--muted); font-size: 13px; margin: 0; }
.err { color: var(--danger); font: 12px var(--mono); }
code { font-family: var(--mono); font-size: 12px; }
</style>
