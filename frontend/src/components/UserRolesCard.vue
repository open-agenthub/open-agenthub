<script setup>
import { onMounted, ref } from 'vue'
import { api } from '../api.js'

const data = ref(null)
const audit = ref([])
const error = ref('')
const busy = ref('')

async function load() {
  error.value = ''
  try {
    const [roles, entries] = await Promise.all([api.adminRoles(), api.adminRoleAudit()])
    data.value = roles
    audit.value = entries
  } catch (e) { error.value = String(e.message || e) }
}
onMounted(load)

async function setRole(u, value) {
  busy.value = u.owner; error.value = ''
  try {
    data.value = await api.setUserRole(u.owner, value || null)
    audit.value = await api.adminRoleAudit()
  } catch (e) {
    // The select already shows the refused value; reload so it snaps back to the stored one.
    await load()
    error.value = String(e.message || e)
  } finally { busy.value = '' }
}

function sourceText(u) {
  switch (u.source) {
    case 'values': return 'Ee:Admins in the Helm values — cannot be changed here'
    case 'user': return 'set here'
    case 'group': return `group ${u.groups.join(', ')}`
    case 'bootstrap': return 'no admin designated yet — everyone is an admin'
    default: return 'default'
  }
}

function fmtDateTime(d) {
  try { return new Date(d).toLocaleString() } catch { return d }
}
const roleText = (r) => r || 'cleared'
</script>

<template>
  <section class="card" data-user-roles>
    <div class="card-head">
      <h4>Admins &amp; roles</h4>
      <span v-if="data?.bootstrap" class="badge warn" data-roles-bootstrap>bootstrap</span>
    </div>
    <p class="note">
      Precedence: <b>Ee:Admins</b> (Helm values, a break-glass admin) → a role <b>set here</b> →
      <b>group</b> mappings<template v-if="!data?.groupMapping"> (Enterprise)</template> → default user.
      A change applies at the user's next request.
    </p>
    <p v-if="data?.bootstrap" class="warn-text">
      No admin is designated, so every signed-in user is an admin. Making someone an admin here
      ends that for everyone else.
    </p>
    <p v-if="error" class="err" data-roles-error>{{ error }}</p>
    <p v-if="!data && !error" class="muted">Loading…</p>
    <div v-else-if="data" class="rows">
      <div v-for="u in data.users" :key="u.owner" class="rrow" :data-role-row="u.owner">
        <div class="rinfo">
          <span class="rname">{{ u.displayName || u.owner }}</span>
          <span class="rmeta">{{ u.owner }} · {{ sourceText(u) }}</span>
        </div>
        <span class="badge" :class="u.role === 'admin' ? 'ok' : 'off'" data-role-badge>{{ u.role }}</span>
        <select :value="u.explicitRole || ''" :disabled="u.source === 'values' || busy === u.owner"
                data-role-select @change="setRole(u, $event.target.value)">
          <option value="">{{ data.groupMapping ? 'groups decide' : 'not set' }}</option>
          <option value="admin">admin</option>
          <option value="user">user</option>
        </select>
      </div>
      <p v-if="!data.users.length" class="muted">No users have signed in yet.</p>
      <p v-for="name in data.valuesAdmins.filter(n => !data.users.some(u => u.owner.toLowerCase() === n.toLowerCase()))"
         :key="name" class="muted">{{ name }} — Ee:Admins, not signed in yet</p>
    </div>
    <details v-if="audit.length" class="audit" data-role-audit>
      <summary>Recent role changes</summary>
      <div v-for="(a, i) in audit.slice(0, 20)" :key="i" class="arow">
        {{ fmtDateTime(a.at) }} · <b>{{ a.actor }}</b> set {{ a.subjectKind }} <b>{{ a.subject }}</b>:
        {{ roleText(a.oldRole) }} → {{ roleText(a.newRole) }}
      </div>
    </details>
  </section>
</template>

<style scoped>
.card { background: var(--panel); border: 1px solid var(--border-2); border-radius: var(--radius-lg); padding: 18px 20px; margin-bottom: 16px; }
.card-head { display: flex; align-items: center; justify-content: space-between; margin-bottom: 6px; }
.card-head h4 { margin: 0; font-size: 15px; }
.badge { font-size: 11px; font-family: var(--mono); padding: 2px 9px; border-radius: 999px; border: 1px solid var(--border); color: var(--muted); }
.badge.ok { color: var(--ok); border-color: var(--ok); }
.badge.warn { color: #d9a13c; border-color: #d9a13c; }
.note { color: var(--muted); font-size: 12px; line-height: 1.5; margin: 0 0 10px; }
.warn-text { color: #d9a13c; font-size: 12px; margin: 0 0 10px; }
.rows { display: flex; flex-direction: column; gap: 8px; }
.rrow { display: flex; align-items: center; gap: 12px; border: 1px solid var(--border); border-radius: 10px; padding: 10px 12px; }
.rinfo { flex: 1; min-width: 0; display: flex; flex-direction: column; gap: 2px; }
.rname { font-weight: 600; }
.rmeta { color: var(--muted); font-size: 11px; font-family: var(--mono); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
select { width: auto; max-width: 140px; flex-shrink: 0; }
.audit { margin-top: 12px; font-size: 12px; color: var(--muted); }
.audit summary { cursor: pointer; }
.arow { padding: 4px 0; border-top: 1px solid var(--border); font-family: var(--mono); font-size: 11px; }
.muted { color: var(--muted); font-size: 12px; margin: 0; }
.err { color: var(--danger); font-family: var(--mono); font-size: 12px; }
</style>
