<script setup>
import { ref, computed, onMounted, watch } from 'vue'
import { api } from '../api.js'
import { isLicenseError } from '../lib/license.js'
import LibraryShareControls from './LibraryShareControls.vue'
import LicenseGate from './LicenseGate.vue'

// Skill library (SKILL.md files) — personal or per project, versioned on every
// save and searchable. Own skills are editable, shared ones read-only.
// Non-admins get a "publish to everyone" toggle when the admin has enabled
// user skill publishing (enterprise).
const props = defineProps({ isAdmin: { type: Boolean, default: false } })

const SKILL_TEMPLATE = `---
name: my-skill
description: One line on when the agent should use this skill.
---

Write the instructions for the agent here.
`

const items = ref([])
const loading = ref(true)
const error = ref('')

const projects = ref([])
const projectNames = computed(() =>
  Object.fromEntries(projects.value.map(p => [p.id, p.name])))

async function load() {
  loading.value = true; error.value = ''
  try { items.value = await api.skills() }
  catch (e) { error.value = String(e.message || e) }
  finally { loading.value = false }
}

async function loadProjects() {
  try { projects.value = await api.listProjects() }
  catch { projects.value = [] }
}

// --- search (server-side: full-text + optional vector similarity) ---
const query = ref('')
const searching = ref(false)
const searchResults = ref(null) // null = not searching, [] = no hits
let searchTimer = null

watch(query, () => {
  clearTimeout(searchTimer)
  const q = query.value.trim()
  if (!q) { searchResults.value = null; return }
  searchTimer = setTimeout(runSearch, 250)
})

async function runSearch() {
  const q = query.value.trim()
  if (!q) { searchResults.value = null; return }
  searching.value = true
  try {
    const hits = await api.searchSkills(q)
    // Keep the row shape of the plain listing (search hits carry no timestamps
    // for shared items we cannot see — merge with the loaded list by id).
    const byId = Object.fromEntries(items.value.map(i => [i.id, i]))
    searchResults.value = hits.map(h => byId[h.id] || h)
  } catch (e) { error.value = String(e.message || e) }
  finally { searching.value = false }
}

const visible = computed(() => searchResults.value ?? items.value)

// --- optional "publish to everyone" toggle (non-admin owners) ---
// Hidden entirely when the enterprise endpoints answer 402 or the admin
// switched user publishing off.
const publishEnabled = ref(false)
const publishState = ref({}) // skill id -> currently published to everyone
const publishError = ref('')
// The toggle only appears once the settings endpoint answered, i.e. with a license; a 402 on
// the toggle itself means the license lapsed since — the gate says so instead of an error line.
const publishLocked = ref(false)

async function loadPublishing() {
  if (props.isAdmin) return // admins use the full sharing expander instead
  try {
    const settings = await api.librarySettings()
    publishEnabled.value = !!settings.userSkillPublishing
  } catch { publishEnabled.value = false; return }
  if (!publishEnabled.value) return
  for (const item of items.value.filter(i => i.mine)) {
    try { publishState.value[item.id] = !!(await api.libraryShares('skills', item.id)).all }
    catch { /* keep the toggle usable with an unknown initial state */ }
  }
}

onMounted(async () => { await Promise.all([load(), loadProjects()]); await loadPublishing() })

async function togglePublish(item, e) {
  const on = e.target.checked
  publishError.value = ''
  try {
    await api.setLibraryShares('skills', item.id, { all: on, users: [], groups: [] })
    publishState.value[item.id] = on
  } catch (err) {
    e.target.checked = !on
    publishState.value[item.id] = !on
    if (isLicenseError(err)) { publishLocked.value = true; return }
    publishError.value = err.status === 403 ? 'Publishing is not allowed on this instance.'
      : String(err.message || err)
  }
}

// --- inline create / edit form ---
const formOpen = ref(false)
const editingId = ref(null)
const form = ref({ name: '', description: '', content: '', projectId: '', comment: '' })
// Extra files (scripts, templates) of the edited skill — read-only in the UI;
// saving without a files payload keeps them (agents manage them via MCP).
const formFiles = ref([])
const formError = ref('')
const saving = ref(false)

function openCreate() {
  editingId.value = null
  form.value = { name: '', description: '', content: SKILL_TEMPLATE, projectId: '', comment: '' }
  formFiles.value = []
  formError.value = ''
  formOpen.value = true
}

async function openEdit(item) {
  formError.value = ''; error.value = ''
  try {
    const full = await api.skill(item.id) // list rows carry no content
    editingId.value = item.id
    form.value = {
      name: full.name,
      description: full.description || '',
      content: full.content || '',
      projectId: full.projectId || '',
      comment: ''
    }
    formFiles.value = full.files || []
    formOpen.value = true
  } catch (e) { error.value = String(e.message || e) }
}

async function save() {
  formError.value = ''
  const name = form.value.name.trim()
  if (!/^[a-z0-9]+(-[a-z0-9]+)*$/.test(name)) {
    formError.value = 'Name must be kebab-case: lowercase letters, digits and hyphens (e.g. review-checklist).'
    return
  }
  saving.value = true
  try {
    const payload = {
      name,
      description: form.value.description.trim(),
      content: form.value.content,
      comment: form.value.comment.trim()
    }
    if (editingId.value) await api.updateSkill(editingId.value, payload)
    else await api.createSkill({ ...payload, projectId: form.value.projectId || null })
    formOpen.value = false
    await load()
    if (searchResults.value) await runSearch()
  } catch (e) { formError.value = String(e.message || e) }
  finally { saving.value = false }
}

async function remove(item) {
  if (!confirm(`Delete skill "${item.name}"?`)) return
  error.value = ''
  try { await api.deleteSkill(item.id); await load() }
  catch (e) { error.value = String(e.message || e) }
}

// --- per-item version history expander ---
const historyOpenId = ref(null)
const historyItems = ref([])
const historyError = ref('')
const historyPreview = ref(null) // { version, content }
const restoring = ref(false)

async function toggleHistory(item) {
  historyPreview.value = null
  historyError.value = ''
  if (historyOpenId.value === item.id) { historyOpenId.value = null; return }
  historyOpenId.value = item.id
  historyItems.value = []
  try { historyItems.value = await api.skillVersions(item.id) }
  catch (e) { historyError.value = String(e.message || e) }
}

async function previewVersion(item, version) {
  historyError.value = ''
  if (historyPreview.value?.version === version) { historyPreview.value = null; return }
  try {
    const detail = await api.skillVersion(item.id, version)
    historyPreview.value = { version, content: detail.content }
  } catch (e) { historyError.value = String(e.message || e) }
}

async function restoreVersion(item, version) {
  historyError.value = ''
  restoring.value = true
  try {
    await api.restoreSkillVersion(item.id, version)
    await load()
    historyItems.value = await api.skillVersions(item.id)
    historyPreview.value = null
  } catch (e) { historyError.value = String(e.message || e) }
  finally { restoring.value = false }
}

// --- per-item sharing expander (admins, own items) ---
const shareOpenId = ref(null)
function toggleShare(item) {
  shareOpenId.value = shareOpenId.value === item.id ? null : item.id
}

function scopeLabel(item) {
  if (!item.mine) return null
  if (!item.projectId) return null
  return projectNames.value[item.projectId] || 'project'
}
</script>

<template>
  <div class="embed">
    <div class="embed-inner">
      <h3 class="pane-head">Skills</h3>
      <p class="note">
        Reusable skills (SKILL.md) your agents can load in sessions — personal or scoped to a
        project, versioned on every save. Agents can also search and upload skills themselves
        through the built-in <span class="mono">skill-library</span> MCP server. Skills shared
        with you by other users are read-only.
      </p>

      <div class="row toolbar">
        <input
          v-model="query"
          data-skill-search
          class="search"
          placeholder="Search skills…" />
        <button v-if="!formOpen" class="primary" data-skill-add @click="openCreate">Add skill</button>
      </div>
      <div v-if="formOpen" class="card form-card" data-skill-form>
        <h4>{{ editingId ? 'Edit skill' : 'New skill' }}</h4>
        <div class="field"><label>Name <span class="dim">— kebab-case: lowercase letters, digits, hyphens</span></label><input v-model="form.name" data-skill-name placeholder="review-checklist" class="mono" /></div>
        <div class="field"><label>Description <span class="dim">— optional</span></label><input v-model="form.description" data-skill-desc placeholder="When should the agent use it?" /></div>
        <div v-if="!editingId && projects.length" class="field">
          <label>Scope <span class="dim">— a project skill is only loaded into that project’s sessions</span></label>
          <select v-model="form.projectId" data-skill-project>
            <option value="">Personal (all my sessions)</option>
            <option v-for="p in projects" :key="p.id" :value="p.id">Project: {{ p.name }}</option>
          </select>
        </div>
        <div class="field">
          <label>Content <span class="dim">— SKILL.md markdown</span></label>
          <textarea v-model="form.content" data-skill-content class="content" :placeholder="SKILL_TEMPLATE"></textarea>
        </div>
        <div v-if="editingId && formFiles.length" class="field" data-skill-files>
          <label>Files <span class="dim">— scripts &amp; assets, kept on save (agents manage them via the skill-library MCP)</span></label>
          <div class="file-chips">
            <span v-for="f in formFiles" :key="f.path" class="pill file mono">{{ f.path }}</span>
          </div>
        </div>
        <div v-if="editingId" class="field">
          <label>Change note <span class="dim">— optional, shown in the version history</span></label>
          <input v-model="form.comment" data-skill-comment placeholder="What changed and why?" />
        </div>
        <p v-if="formError" class="err" data-skill-form-error>{{ formError }}</p>
        <div class="row">
          <button @click="formOpen = false">Cancel</button>
          <button class="primary" data-skill-save :disabled="saving || !form.name.trim()" @click="save">{{ saving ? 'Saving…' : 'Save' }}</button>
        </div>
      </div>

      <p v-if="error" class="err">{{ error }}</p>
      <p v-if="publishError" class="err" data-skill-publish-error>{{ publishError }}</p>
      <LicenseGate v-if="publishLocked" class="gate" data-skill-publish-locked feature="Publishing skills to everyone" />
      <p v-if="loading" class="muted">Loading…</p>
      <p v-else-if="searching" class="muted">Searching…</p>
      <p v-else-if="searchResults && !visible.length" class="muted" data-skill-no-results>No skills match your search.</p>
      <p v-else-if="!visible.length" class="muted">No skills yet — add one to reuse it across sessions.</p>
      <div v-else class="list">
        <div v-for="item in visible" :key="item.id" class="card item" data-skill-row>
          <div class="item-row">
            <div class="item-info">
              <div>
                <span class="item-name mono">{{ item.name }}</span>
                <span class="pill version" data-skill-version>v{{ item.version }}</span>
                <span v-if="scopeLabel(item)" class="pill project" data-skill-project-pill>{{ scopeLabel(item) }}</span>
                <span v-if="!item.mine" class="pill shared" data-skill-shared>shared · {{ item.owner }}</span>
              </div>
              <div v-if="item.description" class="item-desc">{{ item.description }}</div>
            </div>
            <label v-if="!isAdmin && item.mine && publishEnabled" class="check pub">
              <input type="checkbox" :checked="!!publishState[item.id]" data-skill-publish @change="togglePublish(item, $event)" />
              <span>Publish to everyone</span>
            </label>
            <div class="item-actions">
              <button data-skill-history-toggle @click="toggleHistory(item)">History {{ historyOpenId === item.id ? '▾' : '▸' }}</button>
              <template v-if="item.mine">
                <button v-if="isAdmin" data-skill-share-toggle @click="toggleShare(item)">Sharing {{ shareOpenId === item.id ? '▾' : '▸' }}</button>
                <button data-skill-edit @click="openEdit(item)">Edit</button>
                <button class="danger" data-skill-delete @click="remove(item)">Delete</button>
              </template>
            </div>
          </div>
          <div v-if="historyOpenId === item.id" class="history" data-skill-history>
            <p v-if="historyError" class="err" data-skill-history-error>{{ historyError }}</p>
            <p v-else-if="!historyItems.length" class="muted">Loading history…</p>
            <div v-for="v in historyItems" :key="v.version" class="history-row" data-skill-history-row>
              <span class="mono">v{{ v.version }}</span>
              <span class="history-meta">
                {{ new Date(v.createdAt).toLocaleString() }} · {{ v.createdBy }}
                <template v-if="v.comment"> · {{ v.comment }}</template>
              </span>
              <span class="history-actions">
                <button data-skill-version-view @click="previewVersion(item, v.version)">
                  {{ historyPreview?.version === v.version ? 'Hide' : 'View' }}
                </button>
                <button
                  v-if="item.mine && v.version !== item.version"
                  data-skill-version-restore
                  :disabled="restoring"
                  @click="restoreVersion(item, v.version)">Restore</button>
              </span>
            </div>
            <pre v-if="historyPreview" class="preview mono" data-skill-version-preview>{{ historyPreview.content }}</pre>
          </div>
          <div v-if="isAdmin && item.mine && shareOpenId === item.id" data-skill-share-controls>
            <LibraryShareControls kind="skills" :item-id="item.id" />
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
.row.toolbar { justify-content: flex-start; margin-bottom: 16px; }
.search { flex: 1; max-width: 340px; }
.form-card { padding: 18px 20px; margin-bottom: 16px; }
.gate { margin-bottom: 12px; }
.form-card h4 { margin: 0 0 12px; font-size: 15px; }
.dim { color: var(--faint); font-weight: 400; }
.mono { font-family: var(--mono); font-size: 13px; }
.content { min-height: 180px; font-family: var(--mono); font-size: 12px; }
.list { display: flex; flex-direction: column; gap: 10px; }
.item { padding: 14px 16px; }
.item-row { display: flex; align-items: center; gap: 12px; }
.item-info { flex: 1; min-width: 0; }
.item-name { font-weight: 600; color: var(--strong); }
.item-desc { color: var(--muted); font-size: 12px; margin-top: 3px; }
.item-actions { display: flex; gap: 8px; flex-shrink: 0; }
.pill.shared, .pill.version, .pill.project { border: 1px solid var(--border-3); color: var(--muted); font-family: var(--mono); font-weight: 400; margin-left: 8px; }
.file-chips { display: flex; flex-wrap: wrap; gap: 6px; }
.pill.file { border: 1px solid var(--border-3); color: var(--muted); font-weight: 400; padding: 2px 8px; border-radius: 10px; }
.check { display: flex; align-items: center; gap: 7px; font-size: 12px; color: var(--muted); flex-shrink: 0; cursor: pointer; margin: 0; }
.check input { width: auto; }
.history { border-top: 1px solid var(--border-3); margin-top: 12px; padding-top: 10px; }
.history-row { display: flex; align-items: center; gap: 10px; padding: 4px 0; font-size: 12px; }
.history-meta { color: var(--muted); flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.history-actions { display: flex; gap: 6px; flex-shrink: 0; }
.preview { background: var(--bg-2, rgba(127,127,127,.08)); border: 1px solid var(--border-3); border-radius: 6px; padding: 10px 12px; font-size: 11px; max-height: 260px; overflow: auto; white-space: pre-wrap; }
.muted { color: var(--muted); font-size: 12px; }
.err { color: var(--danger); font-family: var(--mono); font-size: 12px; }
@media (max-width: 620px) { .item-row { flex-wrap: wrap; } }
</style>
