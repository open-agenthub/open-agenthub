<script setup>
import { ref, onMounted } from 'vue'
import { api } from '../api.js'
import UserMultiSelect from './UserMultiSelect.vue'

// Inline sharing editor for a library item. Loads current share state and
// IdP groups (via /ee/admin/groups). A 402 from the enterprise share
// endpoints collapses into a short license note instead of crashing the pane.
const props = defineProps({
  kind: { type: String, required: true }, // 'mcp-servers'
  itemId: { type: String, required: true }
})

const loading = ref(true)
const locked = ref(false)
const error = ref('')
const saved = ref(false)
const busy = ref(false)

const all = ref(false)
const selectedUsers = ref([]) // owner strings (multi-select mode)
const usersText = ref('')     // comma-separated fallback when the user list is unavailable
const groupNames = ref([])
const groups = ref([])
const knownUsers = ref([])
const usersLoaded = ref(false)

onMounted(async () => {
  try {
    const [shares, groupList] = await Promise.all([
      api.libraryShares(props.kind, props.itemId),
      api.eeListGroups()
    ])
    all.value = !!shares.all
    selectedUsers.value = [...(shares.users || [])]
    usersText.value = (shares.users || []).join(', ')
    groupNames.value = [...(shares.groups || [])]
    groups.value = groupList || []
  } catch (e) {
    if (e.status === 402) locked.value = true
    else error.value = String(e.message || e)
    loading.value = false
    return
  }
  // Known users power the picker — fall back to a plain comma-separated
  // input when the admin overview is unavailable so nothing breaks.
  try {
    const overview = await api.adminOverview()
    knownUsers.value = (overview?.users || []).map(u => ({
      owner: u.owner,
      displayName: u.displayName,
      email: u.email
    }))
    usersLoaded.value = true
  } catch { usersLoaded.value = false }
  loading.value = false
})

async function save() {
  busy.value = true; error.value = ''; saved.value = false
  try {
    await api.setLibraryShares(props.kind, props.itemId, {
      all: all.value,
      users: usersLoaded.value
        ? selectedUsers.value
        : usersText.value.split(',').map(u => u.trim()).filter(Boolean),
      groups: groupNames.value
    })
    saved.value = true
  } catch (e) {
    if (e.status === 402) locked.value = true
    else error.value = String(e.message || e)
  } finally { busy.value = false }
}
</script>

<template>
  <div class="share-box">
    <p v-if="loading" class="muted">Loading…</p>
    <p v-else-if="locked" class="muted" data-share-locked>Enterprise license required for sharing.</p>
    <template v-else>
      <label class="check">
        <input type="checkbox" v-model="all" data-share-all />
        <span>Share with everyone</span>
      </label>
      <div class="field" data-share-users>
        <label>Users</label>
        <UserMultiSelect v-if="usersLoaded" v-model="selectedUsers" :users="knownUsers" />
        <input v-else v-model="usersText" data-share-users-fallback placeholder="alice, bob" />
      </div>
      <div v-if="groups.length" class="field">
        <label>Groups</label>
        <label v-for="g in groups" :key="g.name" class="check">
          <input type="checkbox" :value="g.name" v-model="groupNames" data-share-group />
          <span>{{ g.name }}</span>
        </label>
      </div>
      <p v-if="error" class="err" data-share-error>{{ error }}</p>
      <div class="row">
        <span v-if="saved" class="ok-text" data-share-saved>Saved ✓</span>
        <button class="primary" data-share-save :disabled="busy" @click="save">{{ busy ? 'Saving…' : 'Save sharing' }}</button>
      </div>
    </template>
  </div>
</template>

<style scoped>
.share-box { border-top: 1px solid var(--border); margin-top: 12px; padding-top: 12px; }
.check { display: flex; align-items: center; gap: 8px; font-size: 13px; color: var(--text); margin-bottom: 8px; cursor: pointer; }
.check input { width: auto; }
.field { margin-bottom: 12px; }
.row { display: flex; align-items: center; justify-content: flex-end; gap: 12px; }
.ok-text { color: var(--ok); font-size: 12px; }
.muted { color: var(--muted); font-size: 12px; margin: 0; }
.err { color: var(--danger); font-family: var(--mono); font-size: 12px; }
</style>
