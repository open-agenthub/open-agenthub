<script setup>
import { computed, onMounted, ref, watch } from 'vue'
import { api } from '../api.js'
import { initials } from '../lib/text.js'

const props = defineProps({ session: Object, embedded: { type: Boolean, default: false } })
const emit = defineEmits(['close'])
const data = ref({ users: [], links: [], policy: { blockedServers: [], blockedTools: [] } })
const recipient = ref(''); const role = ref('Viewer'); const expiresAt = ref('')
const blockedServers = ref(''); const blockedTools = ref(''); const oneTimeUrl = ref(''); const error = ref('')
const linkDrafts = ref({})
const people = computed(() => data.value.users || data.value.directShares || [])
let loadGeneration = 0
const links = computed(() => data.value.links || [])
const serverNames = computed(() => { try { return Object.keys(JSON.parse(props.session.mcpConfigJson || '{}').mcpServers || {}) } catch { return [] } })
function list(value) { return value.split(/[\n,]/).map(item => item.trim()).filter(Boolean) }
async function load() {
  const generation = ++loadGeneration
  const sessionId = props.session.id
  try {
    const result = await api.listSessionShares(sessionId)
    if (generation !== loadGeneration || sessionId !== props.session.id) return
    data.value = result
    linkDrafts.value = Object.fromEntries((result.links || []).map(link => [link.id, { role: link.role, expiresAt: link.expiresAt ? link.expiresAt.slice(0, 16) : '' }]))
    blockedServers.value = (result.policy?.blockedServers || []).join('\n')
    blockedTools.value = (result.policy?.blockedTools || []).join('\n')
  } catch (e) {
    if (generation === loadGeneration) error.value = String(e.message || e)
  }
}
async function addPerson() { if (!recipient.value.trim()) return; await api.createShareUser(props.session.id, { recipient: recipient.value.trim(), role: role.value }); recipient.value = ''; await load() }
async function changePerson(person, value) {
  if (value === 'Remove') { await removePerson(person); return }
  await api.updateShareUser(props.session.id, person.recipient || person.owner, { role: value }); await load()
}
async function removePerson(person) { await api.deleteShareUser(props.session.id, person.recipient || person.owner); await load() }
async function createLink() { const result = await api.createShareLink(props.session.id, { role: role.value, expiresAt: expiresAt.value || null }); oneTimeUrl.value = result.url || result.oneTimeUrl || ''; await load() }
async function saveLink(link) { const draft = linkDrafts.value[link.id]; await api.updateShareLink(props.session.id, link.id, { role: draft.role, expiresAt: draft.expiresAt || null }); await load() }
async function removeLink(link) { await api.deleteShareLink(props.session.id, link.id); await load() }
async function savePolicy() { await api.updateMcpPolicy(props.session.id, { blockedServers: list(blockedServers.value), blockedTools: list(blockedTools.value) }); await load() }
async function copyLink() { await navigator.clipboard?.writeText(oneTimeUrl.value) }
onMounted(load)
function reset() {
  data.value = { users: [], links: [], policy: { blockedServers: [], blockedTools: [] } }
  recipient.value = ''
  role.value = 'Viewer'
  expiresAt.value = ''
  blockedServers.value = ''
  blockedTools.value = ''
  oneTimeUrl.value = ''
  error.value = ''
  linkDrafts.value = {}
  load()
}
watch(() => props.session.id, reset)
</script>
<template>
  <div :class="embedded ? 'embed' : 'overlay'" @click.self="embedded || $emit('close')"><div :class="embedded ? 'embed-inner share-inner' : 'modal'">
    <h3 v-if="!embedded" class="form-title">Share session</h3>
    <p class="note">Roles control terminal input. Session settings, shell access, projects, and sharing remain owner-only.</p>

    <section>
      <div class="add-row"><input v-model="recipient" placeholder="Add people by username…" @keyup.enter="addPerson" /><select v-model="role" class="fit"><option>Viewer</option><option>Collaborator</option></select><button @click="addPerson">Add</button></div>
      <div v-for="person in people" :key="person.recipient || person.owner" class="person">
        <span class="avatar">{{ initials(person.recipient || person.owner) }}</span>
        <span class="pname">{{ person.recipient || person.owner }}</span>
        <select class="fit" :value="person.role" @change="changePerson(person, $event.target.value)"><option>Viewer</option><option>Collaborator</option><option>Remove</option></select>
      </div>
    </section>

    <section class="bordered">
      <div class="link-intro"><div><div class="sect-title">Share by link</div><div class="sub">Anyone with the link — treat it like a secret.</div></div>
        <div class="add-row tight"><select v-model="role" class="fit"><option>Viewer</option><option>Collaborator</option></select><input v-model="expiresAt" class="fit exp" type="datetime-local" /><button @click="createLink">Create link</button></div>
      </div>
      <p v-if="oneTimeUrl" class="one-time">Copy this one-time link now; it cannot be shown again.<button class="copy" @click="copyLink">Copy</button><code>{{ oneTimeUrl }}</code></p>
      <div v-for="link in links" :key="link.id" class="person link-edit">
        <span class="pname mono">{{ link.id }}</span>
        <select v-model="linkDrafts[link.id].role" class="fit" :data-link-role="link.id"><option>Viewer</option><option>Collaborator</option></select>
        <input v-model="linkDrafts[link.id].expiresAt" class="fit exp" :data-link-expiration="link.id" type="datetime-local" />
        <button :data-save-link="link.id" @click="saveLink(link)">Save</button>
        <button class="danger" @click="removeLink(link)">Revoke</button>
      </div>
    </section>

    <section class="bordered">
      <div class="sect-title">MCP security</div>
      <p class="sub">Restrictions apply to every participant, including you, on the next MCP call. Earlier transcript content may already contain tool results.</p>
      <p v-if="serverNames.length" class="servers">Configured servers: {{ serverNames.join(', ') }}</p>
      <div class="field"><label>Blocked servers (one per line)</label><textarea v-model="blockedServers" placeholder="server-name"></textarea></div>
      <div class="field"><label>Blocked exact tool names (one per line)</label><textarea v-model="blockedTools" placeholder="mcp__server__tool"></textarea></div>
      <button @click="savePolicy">Save MCP policy</button>
    </section>

    <p v-if="error" class="err">{{ error }}</p>
    <div v-if="!embedded" class="row"><button @click="$emit('close')">Close</button></div>
  </div></div>
</template>
<style scoped>
.overlay { position: fixed; inset: 0; z-index: 50; display: flex; justify-content: center; overflow-y: auto; padding: 24px; background: rgba(10,9,8,.7); }
.modal { width: 640px; max-width: 100%; padding: 22px; border: 1px solid var(--border-2); border-radius: var(--radius-lg); background: var(--panel); }
.share-inner { padding: 14px 20px 20px; }
.form-title { font-size: 18px; margin: 0 0 6px; }
.note, .sub { color: var(--muted-2); font-size: 12px; line-height: 1.5; margin: 0 0 10px; }
.sub { margin: 2px 0 0; }
.sect-title { font-weight: 600; font-size: 13px; color: var(--text); }
section { margin-top: 6px; }
section.bordered { border-top: 1px solid var(--border); margin-top: 14px; padding-top: 14px; }
.add-row { display: flex; gap: 8px; align-items: center; margin: 8px 0 10px; }
.add-row.tight { margin: 0; flex-wrap: wrap; }
.add-row input { flex: 1; }
.fit { width: auto; }
.exp { min-width: 170px; }
.person { display: flex; gap: 10px; align-items: center; padding: 6px 0; font-size: 13px; }
.avatar { width: 26px; height: 26px; border-radius: 50%; background: #3d3a33; display: inline-flex; align-items: center; justify-content: center; font-size: 10px; color: #d6d1c8; font-weight: 700; flex-shrink: 0; }
.pname { flex: 1; min-width: 0; font-weight: 600; overflow: hidden; text-overflow: ellipsis; }
.mono { font-family: var(--mono); font-size: 12px; font-weight: 400; }
.link-intro { display: flex; gap: 12px; align-items: center; justify-content: space-between; flex-wrap: wrap; }
.one-time { border: 1px solid var(--accent); border-radius: 10px; padding: 10px 12px; font-size: 12px; margin: 10px 0 0; }
.one-time .copy { margin-left: 8px; padding: 3px 10px; font-size: 11px; }
.one-time code { display: block; overflow-wrap: anywhere; margin-top: 6px; color: var(--accent); font-family: var(--mono); }
.servers { font: 12px var(--mono); color: var(--muted-2); }
.row { display: flex; justify-content: flex-end; margin-top: 16px; }
.err { color: var(--danger); font: 12px var(--mono); }
@media (max-width: 760px) { .person.link-edit { align-items: stretch; flex-direction: column; } .person.link-edit select, .person.link-edit input { width: 100%; } }
</style>
