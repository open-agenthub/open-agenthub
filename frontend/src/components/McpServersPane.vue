<script setup>
import { computed, ref, onMounted } from 'vue'
import { api } from '../api.js'
import LibraryShareControls from './LibraryShareControls.vue'

// Personal MCP library (mode=personal) or admin org catalog (mode=org).
// Own / org entries are editable; personal entries shared by others are read-only.
const props = defineProps({
  isAdmin: { type: Boolean, default: false },
  mode: { type: String, default: 'personal' } // 'personal' | 'org'
})

const isOrg = computed(() => props.mode === 'org')

const items = ref([])
const loading = ref(true)
const error = ref('')

async function load() {
  loading.value = true
  error.value = ''
  try {
    items.value = isOrg.value ? await api.adminMcpServers() : await api.mcpServers()
  } catch (e) { error.value = String(e.message || e) }
  finally { loading.value = false }
}
onMounted(load)

const formOpen = ref(false)
const editingId = ref(null)
/** @type {import('vue').Ref<'api' | 'raw'>} */
const formTab = ref('api')
const form = ref(emptyForm())
const formError = ref('')
const saving = ref(false)

function emptyForm() {
  return {
    name: '',
    description: '',
    configJson: '',
    specUrl: '',
    specType: 'auto',
    baseUrl: '',
    secret: ''
  }
}

function openCreate() {
  editingId.value = null
  formTab.value = 'api'
  form.value = emptyForm()
  formError.value = ''
  formOpen.value = true
}

function parseApiConfig(configJson) {
  try {
    const obj = JSON.parse(configJson || '{}')
    return {
      specUrl: obj.specUrl || '',
      specType: obj.specType || 'auto',
      baseUrl: obj.baseUrl || ''
    }
  } catch {
    return { specUrl: '', specType: 'auto', baseUrl: '' }
  }
}

function openEdit(item) {
  editingId.value = item.id
  formTab.value = item.kind === 'api' ? 'api' : 'raw'
  const apiFields = item.kind === 'api' ? parseApiConfig(item.configJson) : { specUrl: '', specType: 'auto', baseUrl: '' }
  form.value = {
    name: item.name,
    description: item.description || '',
    configJson: item.configJson || '',
    specUrl: apiFields.specUrl,
    specType: apiFields.specType,
    baseUrl: apiFields.baseUrl,
    secret: ''
  }
  formError.value = ''
  formOpen.value = true
}

function validateRawConfig(configJson) {
  let parsed
  try { parsed = JSON.parse(configJson) }
  catch { return 'Config is not valid JSON.' }
  if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) {
    return 'Config must be a single JSON object — one server entry like {"type":"http","url":"…"}.'
  }
  return ''
}

function buildApiSecretJson(secret) {
  const trimmed = secret.trim()
  if (!trimmed) return null
  return trimmed.startsWith('{') ? trimmed : JSON.stringify({ token: trimmed })
}

async function save() {
  formError.value = ''
  const name = form.value.name.trim()
  if (!name) { formError.value = 'Name is required.'; return }

  saving.value = true
  try {
    if (formTab.value === 'api') {
      const specUrl = form.value.specUrl.trim()
      if (!specUrl) {
        formError.value = 'Spec URL is required for an API entry.'
        return
      }
      const description = form.value.description.trim()
      const specType = form.value.specType || 'auto'
      const baseUrl = form.value.baseUrl.trim()
      const secret = form.value.secret.trim()

      if (isOrg.value) {
        const config = { specType, specUrl }
        if (baseUrl) config.baseUrl = baseUrl
        const payload = {
          name,
          description,
          kind: 'api',
          configJson: JSON.stringify(config)
        }
        const secretJson = buildApiSecretJson(secret)
        if (secretJson) payload.secretJson = secretJson
        else if (editingId.value) { /* omit — leave existing secret */ }
        if (editingId.value) await api.updateAdminMcpServer(editingId.value, payload)
        else await api.createAdminMcpServer(payload)
      } else if (editingId.value) {
        const config = { specType, specUrl }
        if (baseUrl) config.baseUrl = baseUrl
        const update = {
          name,
          description,
          kind: 'api',
          configJson: JSON.stringify(config)
        }
        if (secret) update.secretJson = buildApiSecretJson(secret)
        await api.updateMcpServer(editingId.value, update)
      } else {
        const payload = {
          name,
          description,
          specUrl,
          specType,
          save: true
        }
        if (baseUrl) payload.baseUrl = baseUrl
        if (secret) payload.secret = secret
        await api.createMcpServerFromApi(payload)
      }
    } else {
      const configErr = validateRawConfig(form.value.configJson)
      if (configErr) { formError.value = configErr; return }
      const payload = {
        name,
        description: form.value.description.trim(),
        kind: 'raw',
        configJson: form.value.configJson
      }
      if (isOrg.value) {
        if (editingId.value) await api.updateAdminMcpServer(editingId.value, payload)
        else await api.createAdminMcpServer(payload)
      } else if (editingId.value) {
        await api.updateMcpServer(editingId.value, payload)
      } else {
        await api.createMcpServer(payload)
      }
    }
    formOpen.value = false
    await load()
  } catch (e) { formError.value = String(e.message || e) }
  finally { saving.value = false }
}

async function remove(item) {
  if (!confirm(`Delete MCP server "${item.name}"? Sessions keep any copy they already use.`)) return
  error.value = ''
  try {
    if (isOrg.value) await api.deleteAdminMcpServer(item.id)
    else await api.deleteMcpServer(item.id)
    await load()
  } catch (e) { error.value = String(e.message || e) }
}

// Sharing expander: personal owners on own items; org catalog always (admin tab).
const shareOpenId = ref(null)
function canShare(item) {
  return isOrg.value || !!item.mine
}
function toggleShare(item) {
  shareOpenId.value = shareOpenId.value === item.id ? null : item.id
}

const title = computed(() => isOrg.value ? 'Org MCP catalog' : 'MCP servers')
const note = computed(() => isOrg.value
  ? 'Organization-wide MCP server entries. Without an enterprise license every signed-in user can use them; with a license, share each entry with everyone, specific users, or IdP groups.'
  : 'Reusable MCP server entries you can attach to any session. Add an OpenAPI/GraphQL API URL (wrapped by AgentHub) or a raw MCP config object. Entries shared with you by other users are read-only.')
</script>

<template>
  <div class="embed">
    <div class="embed-inner">
      <h3 class="pane-head">{{ title }}</h3>
      <p class="note">{{ note }}</p>

      <div v-if="!formOpen" class="row start">
        <button class="primary" data-mcp-add @click="openCreate">Add MCP server</button>
      </div>
      <div v-else class="card form-card" data-mcp-form>
        <h4>{{ editingId ? 'Edit MCP server' : 'New MCP server' }}</h4>
        <div v-if="!editingId" class="tabs" role="tablist">
          <button type="button" class="tab" data-mcp-tab-api :class="{ on: formTab === 'api' }" @click="formTab = 'api'">From API URL</button>
          <button type="button" class="tab" data-mcp-tab-raw :class="{ on: formTab === 'raw' }" @click="formTab = 'raw'">Raw MCP config</button>
        </div>
        <div class="field"><label>Name</label><input v-model="form.name" data-mcp-name placeholder="docs-search" /></div>
        <div class="field"><label>Description <span class="dim">— optional</span></label><input v-model="form.description" data-mcp-desc placeholder="What this server provides" /></div>

        <template v-if="formTab === 'api'">
          <div class="field">
            <label>Spec URL</label>
            <input v-model="form.specUrl" data-mcp-spec-url placeholder="https://…/openapi.json" />
          </div>
          <div class="field">
            <label>Spec type</label>
            <select v-model="form.specType" data-mcp-spec-type>
              <option value="auto">auto</option>
              <option value="openapi">openapi</option>
              <option value="graphql">graphql</option>
            </select>
          </div>
          <div class="field">
            <label>Base URL <span class="dim">— optional override</span></label>
            <input v-model="form.baseUrl" data-mcp-base-url placeholder="https://api.example.com" />
          </div>
          <div class="field">
            <label>Secret <span class="dim">— optional bearer/token{{ editingId ? '; leave empty to keep' : '' }}</span></label>
            <input v-model="form.secret" data-mcp-secret type="password" autocomplete="off" placeholder="token or JSON object" />
          </div>
        </template>
        <template v-else>
          <div class="field">
            <label>Config <span class="dim">— one server entry (JSON object), not a full .mcp.json</span></label>
            <textarea v-model="form.configJson" data-mcp-config placeholder='{ "type": "http", "url": "https://…/sse" }'></textarea>
          </div>
        </template>

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
                <span class="pill kind" data-mcp-kind>{{ item.kind || 'raw' }}</span>
                <span v-if="item.hasSecret" class="pill secret" data-mcp-has-secret>has secret</span>
                <span v-if="!isOrg && !item.mine" class="pill shared" data-mcp-shared>shared · {{ item.owner }}</span>
              </div>
              <div v-if="item.description" class="item-desc">{{ item.description }}</div>
            </div>
            <div v-if="isOrg || item.mine" class="item-actions">
              <button v-if="canShare(item)" data-mcp-share-toggle @click="toggleShare(item)">Sharing {{ shareOpenId === item.id ? '▾' : '▸' }}</button>
              <button data-mcp-edit @click="openEdit(item)">Edit</button>
              <button class="danger" data-mcp-delete @click="remove(item)">Delete</button>
            </div>
          </div>
          <div v-if="canShare(item) && shareOpenId === item.id" data-mcp-share-controls>
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
.tabs { display: flex; gap: 6px; margin-bottom: 14px; }
.tabs .tab { padding: 6px 10px; border-radius: 8px; font-size: 12px; border: 1px solid var(--border); background: none; color: var(--muted); }
.tabs .tab.on { background: var(--panel-2); color: var(--strong); font-weight: 600; border-color: transparent; }
.dim { color: var(--faint); font-weight: 400; }
.list { display: flex; flex-direction: column; gap: 10px; }
.item { padding: 14px 16px; }
.item-row { display: flex; align-items: center; gap: 12px; }
.item-info { flex: 1; min-width: 0; }
.item-name { font-weight: 600; color: var(--strong); }
.item-desc { color: var(--muted); font-size: 12px; margin-top: 3px; }
.item-actions { display: flex; gap: 8px; flex-shrink: 0; }
.pill { border: 1px solid var(--border-3); color: var(--muted); font-family: var(--mono); font-weight: 400; margin-left: 8px; font-size: 11px; padding: 1px 6px; border-radius: 999px; }
.pill.kind { text-transform: lowercase; }
.pill.secret { color: var(--muted-2); }
.pill.shared { }
.muted { color: var(--muted); font-size: 12px; }
.err { color: var(--danger); font-family: var(--mono); font-size: 12px; }
@media (max-width: 620px) { .item-row { flex-wrap: wrap; } }
</style>
