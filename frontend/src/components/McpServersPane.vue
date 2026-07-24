<script setup>
import { ref, onMounted } from 'vue'
import { api } from '../api.js'
import LibraryShareControls from './LibraryShareControls.vue'

// Personal MCP server library: own entries are editable, entries shared by
// other users appear read-only with the owner's name.
const props = defineProps({ isAdmin: { type: Boolean, default: false } })

const items = ref([])
const loading = ref(true)
const error = ref('')

async function load() {
  loading.value = true; error.value = ''
  try { items.value = await api.mcpServers() }
  catch (e) { error.value = String(e.message || e) }
  finally { loading.value = false }
}
onMounted(load)

// --- inline create / edit form ---
const formOpen = ref(false)
const editingId = ref(null)
const form = ref({ name: '', description: '', configJson: '' })
const formError = ref('')
const saving = ref(false)

function openCreate() {
  editingId.value = null
  form.value = { name: '', description: '', configJson: '' }
  formError.value = ''
  formOpen.value = true
}

function openEdit(item) {
  editingId.value = item.id
  form.value = { name: item.name, description: item.description || '', configJson: item.configJson || '' }
  formError.value = ''
  formOpen.value = true
}

async function save() {
  formError.value = ''
  let parsed
  try { parsed = JSON.parse(form.value.configJson) }
  catch { formError.value = 'Config is not valid JSON.'; return }
  if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) {
    formError.value = 'Config must be a single JSON object — one server entry like {"type":"http","url":"…"}.'
    return
  }
  saving.value = true
  try {
    const payload = { name: form.value.name.trim(), description: form.value.description.trim(), configJson: form.value.configJson }
    if (editingId.value) await api.updateMcpServer(editingId.value, payload)
    else await api.createMcpServer(payload)
    formOpen.value = false
    await load()
  } catch (e) { formError.value = String(e.message || e) }
  finally { saving.value = false }
}

async function remove(item) {
  if (!confirm(`Delete MCP server "${item.name}"? Sessions keep any copy they already use.`)) return
  error.value = ''
  try { await api.deleteMcpServer(item.id); await load() }
  catch (e) { error.value = String(e.message || e) }
}

// --- per-item sharing expander (admins, own items) ---
// The controls component loads shares + groups lazily on open.
const shareOpenId = ref(null)
function toggleShare(item) {
  shareOpenId.value = shareOpenId.value === item.id ? null : item.id
}
</script>

<template>
  <div class="embed">
    <div class="embed-inner">
      <h3 class="pane-head">MCP servers</h3>
      <p class="note">
        Reusable MCP server entries you can attach to any session from the "Saved MCP servers"
        picker. Entries shared with you by other users are read-only.
      </p>

      <div v-if="!formOpen" class="row start">
        <button class="primary" data-mcp-add @click="openCreate">Add MCP server</button>
      </div>
      <div v-else class="card form-card" data-mcp-form>
        <h4>{{ editingId ? 'Edit MCP server' : 'New MCP server' }}</h4>
        <div class="field"><label>Name</label><input v-model="form.name" data-mcp-name placeholder="docs-search" /></div>
        <div class="field"><label>Description <span class="dim">— optional</span></label><input v-model="form.description" data-mcp-desc placeholder="What this server provides" /></div>
        <div class="field">
          <label>Config <span class="dim">— one server entry (JSON object), not a full .mcp.json</span></label>
          <textarea v-model="form.configJson" data-mcp-config placeholder='{ "type": "http", "url": "https://…/sse" }'></textarea>
        </div>
        <p v-if="formError" class="err" data-mcp-form-error>{{ formError }}</p>
        <div class="row">
          <button @click="formOpen = false">Cancel</button>
          <button class="primary" data-mcp-save :disabled="saving || !form.name.trim()" @click="save">{{ saving ? 'Saving…' : 'Save' }}</button>
        </div>
      </div>

      <p v-if="error" class="err">{{ error }}</p>
      <p v-if="loading" class="muted">Loading…</p>
      <p v-else-if="!items.length" class="muted">No MCP servers yet — add one to reuse it across sessions.</p>
      <div v-else class="list">
        <div v-for="item in items" :key="item.id" class="card item" data-mcp-row>
          <div class="item-row">
            <div class="item-info">
              <div>
                <span class="item-name">{{ item.name }}</span>
                <span v-if="!item.mine" class="pill shared" data-mcp-shared>shared · {{ item.owner }}</span>
              </div>
              <div v-if="item.description" class="item-desc">{{ item.description }}</div>
            </div>
            <div v-if="item.mine" class="item-actions">
              <button v-if="isAdmin" data-mcp-share-toggle @click="toggleShare(item)">Sharing {{ shareOpenId === item.id ? '▾' : '▸' }}</button>
              <button data-mcp-edit @click="openEdit(item)">Edit</button>
              <button class="danger" data-mcp-delete @click="remove(item)">Delete</button>
            </div>
          </div>
          <div v-if="isAdmin && item.mine && shareOpenId === item.id" data-mcp-share-controls>
            <LibraryShareControls kind="mcp-servers" :item-id="item.id" />
          </div>
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.pane-head { font-size: 22px; margin: 0 0 16px; }
.note { color: var(--muted); font-size: 12px; line-height: 1.5; margin: 0 0 16px; }
.row { display: flex; align-items: center; justify-content: flex-end; gap: 10px; }
.row.start { justify-content: flex-start; margin-bottom: 16px; }
.form-card { padding: 18px 20px; margin-bottom: 16px; }
.form-card h4 { margin: 0 0 12px; font-size: 15px; }
.dim { color: var(--faint); font-weight: 400; }
.list { display: flex; flex-direction: column; gap: 10px; }
.item { padding: 14px 16px; }
.item-row { display: flex; align-items: center; gap: 12px; }
.item-info { flex: 1; min-width: 0; }
.item-name { font-weight: 600; color: var(--strong); }
.item-desc { color: var(--muted); font-size: 12px; margin-top: 3px; }
.item-actions { display: flex; gap: 8px; flex-shrink: 0; }
.pill.shared { border: 1px solid var(--border-3); color: var(--muted); font-family: var(--mono); font-weight: 400; margin-left: 8px; }
.muted { color: var(--muted); font-size: 12px; }
.err { color: var(--danger); font-family: var(--mono); font-size: 12px; }
@media (max-width: 620px) { .item-row { flex-wrap: wrap; } }
</style>
