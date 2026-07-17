<script setup>
import { computed, onMounted, ref } from 'vue'
import { api, auth } from './api.js'
import ProjectSidebar from './components/ProjectSidebar.vue'
import TerminalView from './components/TerminalView.vue'
import NewSessionDialog from './components/NewSessionDialog.vue'
import EditSessionDialog from './components/EditSessionDialog.vue'
import DuplicateSessionDialog from './components/DuplicateSessionDialog.vue'
import ShareSessionDialog from './components/ShareSessionDialog.vue'
import SettingsView from './components/SettingsView.vue'
import AdminView from './components/AdminView.vue'
import SharedSessionView from './components/SharedSessionView.vue'
import { sharedTokenFromPath } from './lib/routes.js'

const sessions = ref([]); const projects = ref([]); const activeId = ref(null); const page = ref(null); const editId = ref(null); const error = ref(''); const isAdmin = ref(false)
const sharedToken = sharedTokenFromPath(location.pathname)
const activeSession = computed(() => sessions.value.find(s => s.id === activeId.value) || null)
const editSession = computed(() => sessions.value.find(s => s.id === editId.value) || null)
const needsLogin = !sharedToken && auth.enabled && !auth.isAuthenticated
async function refresh() { try { [sessions.value, projects.value] = await Promise.all([api.listSessions(), api.listProjects()]) } catch (e) { error.value = String(e.message || e) } }
function selectSession(id) { page.value = null; activeId.value = id }
function openEdit(id) { editId.value = id; page.value = 'edit' }
function openDuplicate(id) { editId.value = id; page.value = 'duplicate' }
function openShare(id) { editId.value = id; page.value = 'share' }
function closePage() { page.value = null; editId.value = null }
async function resume(id) { await api.resumeSession(id); await refresh(); activeId.value = id }
async function pause(id) { await api.pauseSession(id); await refresh() }
async function remove(id) { if (!confirm('Really delete this session? (S3 artifacts are kept)')) return; await api.deleteSession(id); if (activeId.value === id) activeId.value = null; await refresh() }
async function created(session) { closePage(); await refresh(); activeId.value = session.id }
onMounted(async () => { if (needsLogin || sharedToken) return; await refresh(); setInterval(refresh, 5000); try { isAdmin.value = (await api.adminAccess()).isAdmin } catch {} })
</script>
<template>
  <SharedSessionView v-if="sharedToken" :token="sharedToken" />
  <div v-else-if="needsLogin" class="login"><div class="login-card"><h1>Open AgentHub</h1><p>Please sign in to manage your agent sessions.</p><button class="primary" @click="auth.login()">Sign in</button></div></div>
  <div v-else class="shell"><header class="topbar"><div class="brand"><strong>Open AgentHub</strong><span>Agent Control</span></div><div class="actions"><span>{{ auth.user }}</span><button v-if="isAdmin" @click="page = 'admin'">Admin</button><button @click="page = 'settings'">Settings</button><button v-if="auth.enabled" @click="auth.logout()">Sign out</button></div></header>
    <main class="layout"><aside class="sidebar"><ProjectSidebar :projects="projects" :sessions="sessions" :active="activeId" @new="page = 'new'" @select="selectSession" @remove="remove" @resume="resume" @pause="pause" @edit="openEdit" @duplicate="openDuplicate" @share="openShare" @projects-changed="refresh" /><p v-if="error" class="err">{{ error }}</p></aside>
      <section class="content"><SettingsView v-if="page === 'settings'" @close="closePage" /><AdminView v-else-if="page === 'admin'" @close="closePage" /><div v-else-if="page === 'new'" class="page"><NewSessionDialog embedded @close="closePage" @created="created" /></div><div v-else-if="page === 'edit' && editSession" class="page"><EditSessionDialog embedded :session="editSession" :projects="projects" @close="closePage" @updated="created" /></div><div v-else-if="page === 'duplicate' && editSession" class="page"><DuplicateSessionDialog embedded :session="editSession" :projects="projects" @close="closePage" @duplicated="created" /></div><div v-else-if="page === 'share' && editSession" class="page"><ShareSessionDialog embedded :session="editSession" @close="closePage" /></div><TerminalView v-else-if="activeSession" :session="activeSession" @back="activeId = null" @resume="resume" @pause="pause" /><div v-else class="empty">Select a session on the left or start a new one.</div></section>
    </main></div>
</template>
<style scoped>
.login { height: 100%; display: flex; align-items: center; justify-content: center; } .login-card { max-width: 360px; padding: 28px; border: 1px solid var(--border); border-radius: var(--radius); background: var(--panel); } .shell { height: 100%; display: flex; flex-direction: column; } .topbar { display: flex; justify-content: space-between; align-items: center; padding: 12px 18px; border-bottom: 1px solid var(--border); background: var(--panel); } .brand { display: flex; gap: 10px; align-items: baseline; } .brand span, .actions span { color: var(--muted); font: 12px var(--mono); } .actions { display: flex; gap: 8px; align-items: center; } .layout { display: flex; flex: 1; min-height: 0; } .sidebar { display: flex; flex-direction: column; width: 340px; border-right: 1px solid var(--border); background: var(--panel); min-height: 0; } .content { display: flex; flex: 1; min-width: 0; } .page { display: flex; flex: 1; min-width: 0; } .empty { margin: auto; color: var(--muted); padding: 24px; text-align: center; } .err { color: var(--danger); padding: 0 16px; font: 12px var(--mono); } @media (max-width: 760px) { .layout { flex-direction: column; } .sidebar { width: 100%; border: 0; } .content { min-height: 55%; } .actions span { display: none; } }
</style>
