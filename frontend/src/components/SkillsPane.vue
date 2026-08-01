<script setup>
import { ref, onMounted } from 'vue'
import { api } from '../api.js'
import LibraryShareControls from './LibraryShareControls.vue'

// Personal skill library (SKILL.md files). Own skills are editable, shared
// ones read-only. Non-admins get a "publish to everyone" toggle when the
// admin has enabled user skill publishing (enterprise).
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

async function load() {
  loading.value = true; error.value = ''
  try { items.value = await api.skills() }
  catch (e) { error.value = String(e.message || e) }
  finally { loading.value = false }
}

// --- optional "publish to everyone" toggle (non-admin owners) ---
// Hidden entirely when the enterprise endpoints answer 402 or the admin
// switched user publishing off.
const publishEnabled = ref(false)
const publishState = ref({}) // skill id -> currently published to everyone
const publishError = ref('')

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

onMounted(async () => { await load(); await loadPublishing() })

async function togglePublish(item, e) {
  const on = e.target.checked
  publishError.value = ''
  try {
    await api.setLibraryShares('skills', item.id, { all: on, users: [], groups: [] })
    publishState.value[item.id] = on
  } catch (err) {
    e.target.checked = !on
    publishState.value[item.id] = !on
    publishError.value = err.status === 402 ? 'Enterprise license required for publishing.'
      : err.status === 403 ? 'Publishing is not allowed on this instance.'
      : String(err.message || err)
  }
}

// --- inline create / edit form ---
const formOpen = ref(false)
const editingId = ref(null)
const form = ref({ name: '', description: '', content: '' })
const formError = ref('')
const saving = ref(false)

function openCreate() {
  editingId.value = null
  form.value = { name: '', description: '', content: SKILL_TEMPLATE }
  formError.value = ''
  formOpen.value = true
}

async function openEdit(item) {
  formError.value = ''; error.value = ''
  try {
    const full = await api.skill(item.id) // list rows carry no content
    editingId.value = item.id
    form.value = { name: full.name, description: full.description || '', content: full.content || '' }
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
    const payload = { name, description: form.value.description.trim(), content: form.value.content }
    if (editingId.value) await api.updateSkill(editingId.value, payload)
    else await api.createSkill(payload)
    formOpen.value = false
    await load()
  } catch (e) { formError.value = String(e.message || e) }
  finally { saving.value = false }
}

async function remove(item) {
  if (!confirm(`Delete skill "${item.name}"?`)) return
  error.value = ''
  try { await api.deleteSkill(item.id); await load() }
  catch (e) { error.value = String(e.message || e) }
}

// --- per-item sharing expander (admins, own items) ---
const shareOpenId = ref(null)
function toggleShare(item) {
  shareOpenId.value = shareOpenId.value === item.id ? null : item.id
}
</script>

<template>
  <div class="embed">
    <div class="embed-inner">
      <h3 class="pane-head">Skills</h3>
      <p class="note">
        Reusable skills (SKILL.md) your agents can load in sessions. Skills shared with you
        by other users are read-only.
      </p>

      <div v-if="!formOpen" class="row start">
        <button class="primary" data-skill-add @click="openCreate">Add skill</button>
      </div>
      <div v-else class="card form-card" data-skill-form>
        <h4>{{ editingId ? 'Edit skill' : 'New skill' }}</h4>
        <div class="field"><label>Name <span class="dim">— kebab-case: lowercase letters, digits, hyphens</span></label><input v-model="form.name" data-skill-name placeholder="review-checklist" class="mono" /></div>
        <div class="field"><label>Description <span class="dim">— optional</span></label><input v-model="form.description" data-skill-desc placeholder="When should the agent use it?" /></div>
        <div class="field">
          <label>Content <span class="dim">— SKILL.md markdown</span></label>
          <textarea v-model="form.content" data-skill-content class="content" :placeholder="SKILL_TEMPLATE"></textarea>
        </div>
        <p v-if="formError" class="err" data-skill-form-error>{{ formError }}</p>
        <div class="row">
          <button @click="formOpen = false">Cancel</button>
          <button class="primary" data-skill-save :disabled="saving || !form.name.trim()" @click="save">{{ saving ? 'Saving…' : 'Save' }}</button>
        </div>
      </div>

      <p v-if="error" class="err">{{ error }}</p>
      <p v-if="publishError" class="err" data-skill-publish-error>{{ publishError }}</p>
      <p v-if="loading" class="muted">Loading…</p>
      <p v-else-if="!items.length" class="muted">No skills yet — add one to reuse it across sessions.</p>
      <div v-else class="list">
        <div v-for="item in items" :key="item.id" class="card item" data-skill-row>
          <div class="item-row">
            <div class="item-info">
              <div>
                <span class="item-name mono">{{ item.name }}</span>
                <span v-if="!item.mine" class="pill shared" data-skill-shared>shared · {{ item.owner }}</span>
              </div>
              <div v-if="item.description" class="item-desc">{{ item.description }}</div>
            </div>
            <label v-if="!isAdmin && item.mine && publishEnabled" class="check pub">
              <input type="checkbox" :checked="!!publishState[item.id]" data-skill-publish @change="togglePublish(item, $event)" />
              <span>Publish to everyone</span>
            </label>
            <div v-if="item.mine" class="item-actions">
              <button v-if="isAdmin" data-skill-share-toggle @click="toggleShare(item)">Sharing {{ shareOpenId === item.id ? '▾' : '▸' }}</button>
              <button data-skill-edit @click="openEdit(item)">Edit</button>
              <button class="danger" data-skill-delete @click="remove(item)">Delete</button>
            </div>
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
.row.start { justify-content: flex-start; margin-bottom: 16px; }
.form-card { padding: 18px 20px; margin-bottom: 16px; }
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
.pill.shared { border: 1px solid var(--border-3); color: var(--muted); font-family: var(--mono); font-weight: 400; margin-left: 8px; }
.check { display: flex; align-items: center; gap: 7px; font-size: 12px; color: var(--muted); flex-shrink: 0; cursor: pointer; margin: 0; }
.check input { width: auto; }
.muted { color: var(--muted); font-size: 12px; }
.err { color: var(--danger); font-family: var(--mono); font-size: 12px; }
@media (max-width: 620px) { .item-row { flex-wrap: wrap; } }
</style>
