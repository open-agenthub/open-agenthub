<script setup>
import { computed, onMounted, ref } from 'vue'
import { api } from '../api.js'

const props = defineProps({ session: Object, embedded: { type: Boolean, default: false } })
const emit = defineEmits(['close'])
const data = ref({ users: [], links: [], policy: { blockedServers: [], blockedTools: [] } })
const recipient = ref(''); const role = ref('Viewer'); const expiresAt = ref('')
const blockedServers = ref(''); const blockedTools = ref(''); const oneTimeUrl = ref(''); const error = ref('')
const linkDrafts = ref({})
const people = computed(() => data.value.users || data.value.directShares || [])
const links = computed(() => data.value.links || [])
const serverNames = computed(() => { try { return Object.keys(JSON.parse(props.session.mcpConfigJson || '{}').mcpServers || {}) } catch { return [] } })
function list(value) { return value.split(/[\n,]/).map(item => item.trim()).filter(Boolean) }
async function load() {
  try { data.value = await api.listSessionShares(props.session.id); linkDrafts.value = Object.fromEntries((data.value.links || []).map(link => [link.id, { role: link.role, expiresAt: link.expiresAt ? link.expiresAt.slice(0, 16) : '' }])); blockedServers.value = (data.value.policy?.blockedServers || []).join('\n'); blockedTools.value = (data.value.policy?.blockedTools || []).join('\n') }
  catch (e) { error.value = String(e.message || e) }
}
async function addPerson() { if (!recipient.value.trim()) return; await api.createShareUser(props.session.id, { recipient: recipient.value.trim(), role: role.value }); recipient.value = ''; await load() }
async function changePerson(person, value) { await api.updateShareUser(props.session.id, person.recipient || person.owner, { role: value }); await load() }
async function removePerson(person) { await api.deleteShareUser(props.session.id, person.recipient || person.owner); await load() }
async function createLink() { const result = await api.createShareLink(props.session.id, { role: role.value, expiresAt: expiresAt.value || null }); oneTimeUrl.value = result.url || result.oneTimeUrl || ''; await load() }
async function saveLink(link) { const draft = linkDrafts.value[link.id]; await api.updateShareLink(props.session.id, link.id, { role: draft.role, expiresAt: draft.expiresAt || null }); await load() }
async function removeLink(link) { await api.deleteShareLink(props.session.id, link.id); await load() }
async function savePolicy() { await api.updateMcpPolicy(props.session.id, { blockedServers: list(blockedServers.value), blockedTools: list(blockedTools.value) }); await load() }
async function copyLink() { await navigator.clipboard?.writeText(oneTimeUrl.value) }
onMounted(load)
</script>
<template>
  <div :class="embedded ? 'embed' : 'overlay'" @click.self="embedded || $emit('close')"><div :class="embedded ? 'embed-inner' : 'modal'"><h3>Share session</h3><p class="note">Roles control terminal input. Session settings, shell access, projects, and sharing remain owner-only.</p>
    <section><h4>People</h4><div class="inline"><input v-model="recipient" placeholder="Known user" /><select v-model="role"><option>Viewer</option><option>Collaborator</option></select><button @click="addPerson">Add</button></div><div v-for="person in people" :key="person.recipient || person.owner" class="item"><span>{{ person.recipient || person.owner }}</span><select :value="person.role" @change="changePerson(person, $event.target.value)"><option>Viewer</option><option>Collaborator</option></select><button class="danger" @click="removePerson(person)">Revoke</button></div></section>
    <section><h4>Secret links</h4><div class="inline"><select v-model="role"><option>Viewer</option><option>Collaborator</option></select><input v-model="expiresAt" type="datetime-local" /><button @click="createLink">Create link</button></div><p v-if="oneTimeUrl" class="one-time">Copy this one-time link now; it cannot be shown again.<button @click="copyLink">Copy</button><code>{{ oneTimeUrl }}</code></p><div v-for="link in links" :key="link.id" class="item link-edit"><span>{{ link.id }}</span><select v-model="linkDrafts[link.id].role" :data-link-role="link.id"><option>Viewer</option><option>Collaborator</option></select><input v-model="linkDrafts[link.id].expiresAt" :data-link-expiration="link.id" type="datetime-local" /><button :data-save-link="link.id" @click="saveLink(link)">Save</button><button class="danger" @click="removeLink(link)">Revoke</button></div></section>
    <section><h4>MCP security</h4><p class="note">Restrictions apply to every participant, including you, on the next MCP call. Earlier transcript content may already contain tool results.</p><p v-if="serverNames.length" class="servers">Configured servers: {{ serverNames.join(', ') }}</p><div class="field"><label>Blocked servers (one per line)</label><textarea v-model="blockedServers" placeholder="server-name"></textarea></div><div class="field"><label>Blocked exact tool names (one per line)</label><textarea v-model="blockedTools" placeholder="mcp__server__tool"></textarea></div><button @click="savePolicy">Save MCP policy</button></section>
    <p v-if="error" class="err">{{ error }}</p><div class="row"><button @click="$emit('close')">Close</button></div>
  </div></div>
</template>
<style scoped>
.overlay { position: fixed; inset: 0; z-index: 50; display: flex; justify-content: center; overflow-y: auto; padding: 24px; background: rgba(5,7,10,.7); } .modal { width: 660px; max-width: 100%; padding: 22px; border: 1px solid var(--border); border-radius: 14px; background: var(--panel); } h3 { margin: 0 0 6px; } h4 { margin: 18px 0 8px; font-size: 14px; } section { border-top: 1px solid var(--border); } .note { color: var(--muted); font-size: 12px; line-height: 1.5; } .inline, .item { display: flex; gap: 8px; align-items: center; margin: 7px 0; } .inline input { flex: 1; } .inline select, .item select { width: auto; } .item { justify-content: space-between; font-size: 13px; } .item span { overflow: hidden; text-overflow: ellipsis; } .link-edit input { min-width: 190px; } .one-time { border: 1px solid var(--accent); padding: 10px; font-size: 12px; } code { display: block; overflow-wrap: anywhere; margin-top: 6px; color: var(--accent); } .servers { font: 12px var(--mono); color: var(--muted); } .row { display: flex; justify-content: flex-end; margin-top: 18px; } .err { color: var(--danger); font: 12px var(--mono); }
@media (max-width: 760px) { .link-edit { align-items: stretch; flex-direction: column; } .link-edit select, .link-edit input { width: 100%; } }
</style>
