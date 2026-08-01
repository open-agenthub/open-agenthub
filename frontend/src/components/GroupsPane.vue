<script setup>
import { ref, onMounted } from 'vue'
import { api } from '../api.js'
import UserMultiSelect from './UserMultiSelect.vue'

// Admin pane: user groups for library sharing plus the instance-wide
// "users may publish skills to everyone" switch. Everything here is
// enterprise — a 402 renders one locked card instead of broken controls.
const groups = ref([])
const loading = ref(true)
const locked = ref(false)
const error = ref('')

const publishing = ref(false)
const pubMsg = ref('')

const newName = ref('')
const creating = ref(false)
const createError = ref('')

const membersSel = ref({})  // group id -> [owner, …] (multi-select mode)
const membersText = ref({}) // group id -> comma-separated fallback
const memberBusy = ref({})
const memberMsg = ref({})   // group id -> { ok, text }

// Known users for the member picker — a failure falls back to plain text input.
const knownUsers = ref([])
const usersLoaded = ref(false)

async function load() {
  loading.value = true; error.value = ''
  try {
    const [groupList, settings] = await Promise.all([api.libraryGroups(), api.librarySettings()])
    groups.value = groupList
    publishing.value = !!settings.userSkillPublishing
    const sel = {}; const text = {}
    for (const g of groupList) {
      sel[g.id] = [...(g.members || [])]
      text[g.id] = (g.members || []).join(', ')
    }
    membersSel.value = sel
    membersText.value = text
  } catch (e) {
    if (e.status === 402) locked.value = true
    else error.value = String(e.message || e)
    loading.value = false
    return
  }
  try {
    knownUsers.value = await api.libraryUsers()
    usersLoaded.value = true
  } catch { usersLoaded.value = false }
  loading.value = false
}
onMounted(load)

async function create() {
  const name = newName.value.trim()
  if (!name) return
  creating.value = true; createError.value = ''
  try {
    await api.createLibraryGroup({ name })
    newName.value = ''
    await load()
  } catch (e) {
    if (e.status === 402) locked.value = true
    else createError.value = String(e.message || e)
  } finally { creating.value = false }
}

async function saveMembers(g) {
  memberBusy.value[g.id] = true
  memberMsg.value[g.id] = null
  try {
    const members = usersLoaded.value
      ? membersSel.value[g.id] || []
      : (membersText.value[g.id] || '').split(',').map(m => m.trim()).filter(Boolean)
    const updated = await api.setLibraryGroupMembers(g.id, { members })
    groups.value = groups.value.map(x => x.id === g.id ? updated : x)
    membersSel.value[g.id] = [...(updated.members || [])]
    membersText.value[g.id] = (updated.members || []).join(', ')
    memberMsg.value[g.id] = { ok: true, text: 'Saved ✓' }
  } catch (e) {
    memberMsg.value[g.id] = { ok: false, text: String(e.message || e) } // 400 = unknown users
  } finally { memberBusy.value[g.id] = false }
}

async function remove(g) {
  if (!confirm(`Delete group "${g.name}"? Items shared with it stop being visible to its members.`)) return
  error.value = ''
  try { await api.deleteLibraryGroup(g.id); await load() }
  catch (e) { error.value = String(e.message || e) }
}

async function togglePublishing(e) {
  const on = e.target.checked
  pubMsg.value = ''
  try {
    await api.setLibrarySettings({ userSkillPublishing: on })
    publishing.value = on
    pubMsg.value = 'Saved ✓'
  } catch (err) {
    e.target.checked = !on
    publishing.value = !on
    if (err.status === 402) locked.value = true
    else error.value = String(err.message || err)
  }
}
</script>

<template>
  <div class="embed">
    <div class="embed-inner">
      <h3 class="pane-head">Groups &amp; sharing</h3>
      <p v-if="loading" class="muted">Loading…</p>

      <!-- Enterprise gate: same locked style as the admin license card. -->
      <section v-else-if="locked" class="card cta" data-library-locked>
        <div class="card-head">
          <h4>Library sharing is an enterprise feature</h4>
          <span class="badge off">unlicensed</span>
        </div>
        <p class="note">
          Personal MCP servers and skills stay free for every user. An enterprise license adds
          user groups and lets admins share library items with individual users, groups, or
          everyone. Activate a license under the <b>License</b> tab to unlock this pane.
        </p>
      </section>

      <template v-else>
        <p v-if="error" class="err">{{ error }}</p>

        <section class="card sect">
          <div class="card-head"><h4>Skill publishing</h4></div>
          <p class="note">When enabled, regular users can publish their own skills to everyone. Sharing with specific users or groups stays admin-only.</p>
          <label class="check">
            <input type="checkbox" :checked="publishing" data-skill-publishing-toggle @change="togglePublishing" />
            <span>Allow users to publish skills to everyone</span>
          </label>
          <p v-if="pubMsg" class="ok-text" data-publishing-msg>{{ pubMsg }}</p>
        </section>

        <section class="card sect">
          <div class="card-head"><h4>User groups</h4></div>
          <p class="note">Groups make sharing library items with many users easy — pick the members per group below.</p>
          <div class="create">
            <input v-model="newName" data-group-name placeholder="Group name (e.g. platform-team)" :disabled="creating" @keyup.enter="create" />
            <button class="primary" data-group-create :disabled="creating || !newName.trim()" @click="create">{{ creating ? 'Creating…' : 'Create group' }}</button>
          </div>
          <p v-if="createError" class="err" data-group-create-error>{{ createError }}</p>

          <p v-if="!groups.length" class="muted">No groups yet.</p>
          <div v-for="g in groups" :key="g.id" class="group" data-group-row>
            <div class="group-head">
              <span class="group-name">{{ g.name }}</span>
              <span class="muted mono">{{ (g.members || []).length }} member{{ (g.members || []).length === 1 ? '' : 's' }}</span>
              <button class="danger" data-group-delete @click="remove(g)">Delete</button>
            </div>
            <div class="field" data-group-members>
              <label>Members</label>
              <UserMultiSelect v-if="usersLoaded" v-model="membersSel[g.id]" :users="knownUsers" />
              <input v-else v-model="membersText[g.id]" data-group-members-fallback placeholder="alice, bob" />
            </div>
            <div class="row">
              <span v-if="memberMsg[g.id]" :class="memberMsg[g.id].ok ? 'ok-text' : 'err'" data-group-members-msg>{{ memberMsg[g.id].text }}</span>
              <button class="primary" data-group-save-members :disabled="memberBusy[g.id]" @click="saveMembers(g)">{{ memberBusy[g.id] ? 'Saving…' : 'Save members' }}</button>
            </div>
          </div>
        </section>
      </template>
    </div>
  </div>
</template>

<style scoped>
.pane-head { font-size: 22px; margin: 0 0 16px; }
.sect { padding: 18px 20px; margin-bottom: 16px; }
.cta { border-color: #4a3e1e; background: #1f1b12; padding: 18px 20px; }
.card-head { display: flex; align-items: center; justify-content: space-between; margin-bottom: 6px; }
.card-head h4 { margin: 0; font-size: 15px; }
.badge { font-size: 11px; font-family: var(--mono); padding: 2px 9px; border-radius: 999px; border: 1px solid var(--border); color: var(--muted); }
.badge.off { color: var(--muted); }
.note { color: var(--muted); font-size: 12px; line-height: 1.5; margin: 0 0 14px; }
.check { display: flex; align-items: center; gap: 8px; font-size: 13px; color: var(--text); cursor: pointer; }
.check input { width: auto; }
.create { display: flex; gap: 10px; margin-bottom: 12px; }
.create input { flex: 1; }
.group { border: 1px solid var(--border); border-radius: 10px; padding: 12px 14px; margin-bottom: 10px; }
.group-head { display: flex; align-items: center; gap: 12px; margin-bottom: 10px; }
.group-name { font-weight: 600; color: var(--strong); flex: 1; min-width: 0; }
.field { margin-bottom: 10px; }
.dim { color: var(--faint); font-weight: 400; }
.mono { font-family: var(--mono); font-size: 11px; }
.row { display: flex; align-items: center; justify-content: flex-end; gap: 12px; }
.ok-text { color: var(--ok); font-size: 12px; margin: 0; }
.muted { color: var(--muted); font-size: 12px; }
.err { color: var(--danger); font-family: var(--mono); font-size: 12px; }
</style>
