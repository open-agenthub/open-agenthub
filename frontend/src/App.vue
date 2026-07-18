<script setup>
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { api, auth } from './api.js'
import ProjectSidebar from './components/ProjectSidebar.vue'
import TerminalView from './components/TerminalView.vue'
import HomeView from './components/HomeView.vue'
import SessionsView from './components/SessionsView.vue'
import UsageView from './components/UsageView.vue'
import NewSessionDialog from './components/NewSessionDialog.vue'
import EditSessionDialog from './components/EditSessionDialog.vue'
import DuplicateSessionDialog from './components/DuplicateSessionDialog.vue'
import ShareSessionDialog from './components/ShareSessionDialog.vue'
import SettingsView from './components/SettingsView.vue'
import AdminView from './components/AdminView.vue'
import SharedSessionView from './components/SharedSessionView.vue'
import { sharedTokenFromPath } from './lib/routes.js'
import { initials } from './lib/text.js'

const sessions = ref([])
const projects = ref([])
const activeId = ref(null)
const page = ref(null)
const editId = ref(null)
const error = ref('')
const isAdmin = ref(false)
const settingsTab = ref('credentials')
const query = ref('')
const searchBox = ref(null)
const sharedToken = sharedTokenFromPath(location.pathname)
const activeSession = computed(() => sessions.value.find(session => session.id === activeId.value) || null)
const editSession = computed(() => sessions.value.find(session => session.id === editId.value) || null)
const needsLogin = !sharedToken && auth.enabled && !auth.isAuthenticated
const waitingCount = computed(() => sessions.value.filter(s => s.questionPending).length)
let refreshTimer

function sessionIdFromLocation() {
  const match = location.pathname.match(/^\/s\/([^/]+)\/?$/)
  if (match) {
    try { return decodeURIComponent(match[1]) } catch { return null }
  }
  return new URLSearchParams(location.search).get('session')
}

async function refresh() {
  try {
    [sessions.value, projects.value] = await Promise.all([api.listSessions(), api.listProjects()])
  } catch (e) {
    error.value = String(e.message || e)
  }
}

function goHome() { page.value = null; activeId.value = null }
function selectSession(id) { page.value = null; activeId.value = id }
function openPage(name) { page.value = name; activeId.value = null }
function openEdit(id) { editId.value = id; page.value = 'edit' }
function openDuplicate(id) { editId.value = id; page.value = 'duplicate' }
function openShare(id) { editId.value = id; page.value = 'share' }
function openSettings(tab = 'credentials') { settingsTab.value = tab; page.value = 'settings' }
function closePage() { page.value = null; editId.value = null }
async function resume(id) { await api.resumeSession(id); await refresh(); activeId.value = id }
async function pause(id) { await api.pauseSession(id); await refresh() }
async function remove(id) { if (!confirm('Really delete this session? (S3 artifacts are kept)')) return; await api.deleteSession(id); if (activeId.value === id) activeId.value = null; await refresh() }
async function created(session) { closePage(); await refresh(); activeId.value = session.id }

function restoreLocation() {
  page.value = location.pathname === '/account' ? 'settings' : null
  if (page.value === 'settings') settingsTab.value = 'account'
  activeId.value = sessionIdFromLocation()
}

function onKey(e) {
  if ((e.metaKey || e.ctrlKey) && e.key.toLowerCase() === 'k') { e.preventDefault(); searchBox.value?.focus() }
}

watch(activeId, id => {
  const target = id ? `/s/${encodeURIComponent(id)}` : '/'
  if (!id && location.pathname === '/account') return
  if (location.pathname + location.search !== target) history.pushState({}, '', target)
})

onMounted(async () => {
  if (needsLogin || sharedToken) return
  restoreLocation()
  window.addEventListener('popstate', restoreLocation)
  window.addEventListener('keydown', onKey)
  await refresh()
  refreshTimer = setInterval(refresh, 5000)
  try { isAdmin.value = (await api.adminAccess()).isAdmin } catch {}
})

onBeforeUnmount(() => {
  window.removeEventListener('popstate', restoreLocation)
  window.removeEventListener('keydown', onKey)
  if (refreshTimer) clearInterval(refreshTimer)
})
</script>
<template>
  <SharedSessionView v-if="sharedToken" :token="sharedToken" />
  <div v-else-if="needsLogin" class="login"><div class="login-card"><h1>Open AgentHub</h1><p>Please sign in to manage your agent sessions.</p><button class="primary" @click="auth.login()">Sign in</button></div></div>
  <div v-else class="shell">
    <aside class="side">
      <div class="brand" @click="goHome"><img src="/favicon.svg" alt="" class="logo" /><span>Open AgentHub</span></div>
      <button class="primary task-btn" @click="openPage('new')">+ Give an agent a task</button>
      <nav class="nav">
        <button class="nav-item" :class="{ on: !page && !activeId }" @click="goHome">◈ Home</button>
        <button class="nav-item" :class="{ on: page === 'usage' }" @click="openPage('usage')">∿ Usage &amp; cost</button>
      </nav>
      <div class="sessions-head" :class="{ on: page === 'sessions' }" role="button" @click="openPage('sessions')">
        <span>SESSIONS</span>
        <span v-if="waitingCount" class="wait-badge">{{ waitingCount }}</span>
      </div>
      <ProjectSidebar class="side-sessions" :projects="projects" :sessions="sessions" :active="activeId" :query="query" @new="openPage('new')" @select="selectSession" @remove="remove" @resume="resume" @pause="pause" @edit="openEdit" @duplicate="openDuplicate" @share="openShare" @projects-changed="refresh" />
      <p v-if="error" class="err">{{ error }}</p>
      <div class="side-foot">Open AgentHub · self-hosted</div>
    </aside>
    <div class="main">
      <header class="topbar">
        <div class="search"><span class="search-icon">⌕</span><input ref="searchBox" v-model="query" placeholder="Find a session…" /><span class="kbd">⌘K</span></div>
        <span class="spacer"></span>
        <button class="icon-btn" :class="{ on: page === 'settings' }" title="Settings" @click="openSettings()">⚙</button>
        <button v-if="auth.enabled" class="ghost" title="Sign out" @click="auth.logout()">Sign out</button>
        <span class="avatar" :title="auth.user" @click="openSettings()">{{ initials(auth.user) }}</span>
      </header>
      <section class="content">
        <SettingsView v-if="page === 'settings'" :initial-tab="settingsTab" :is-admin="isAdmin" @close="closePage" />
        <AdminView v-else-if="page === 'admin'" @close="closePage" />
        <div v-else-if="page === 'new'" class="page"><NewSessionDialog embedded :projects="projects" @close="closePage" @created="created" /></div>
        <div v-else-if="page === 'edit' && editSession" class="page"><EditSessionDialog :key="editSession.id" embedded :session="editSession" :projects="projects" @close="closePage" @updated="created" /></div>
        <div v-else-if="page === 'duplicate' && editSession" class="page"><DuplicateSessionDialog :key="editSession.id" embedded :session="editSession" :projects="projects" @close="closePage" @duplicated="created" /></div>
        <div v-else-if="page === 'share' && editSession" class="page"><ShareSessionDialog :key="editSession.id" embedded :session="editSession" @close="closePage" /></div>
        <TerminalView v-else-if="activeSession" :key="activeSession.id" :session="activeSession" @back="activeId = null" @resume="resume" @pause="pause" @edit="openEdit" @duplicate="openDuplicate" />
        <SessionsView v-else-if="page === 'sessions'" :sessions="sessions" :projects="projects" :query="query" @select="selectSession" @new="openPage('new')" @remove="remove" @resume="resume" @pause="pause" @edit="openEdit" @duplicate="openDuplicate" />
        <UsageView v-else-if="page === 'usage'" />
        <HomeView v-else :sessions="sessions" @select="selectSession" @sessions="openPage('sessions')" @usage="openPage('usage')" @new="openPage('new')" @resume="resume" />
      </section>
    </div>
  </div>
</template>
<style scoped>
.login { height: 100%; display: flex; align-items: center; justify-content: center; }
.login-card { max-width: 380px; padding: 30px; border: 1px solid var(--border-2); border-radius: var(--radius-lg); background: var(--panel); text-align: center; }
.login-card h1 { font-size: 24px; margin: 0 0 8px; }
.login-card p { color: var(--muted-2); margin: 0 0 18px; }
.shell { height: 100%; display: flex; }

.side { width: 248px; flex-shrink: 0; background: var(--sidebar); border-right: 1px solid var(--border); display: flex; flex-direction: column; padding: 18px 12px 14px; min-height: 0; }
.brand { display: flex; align-items: center; gap: 10px; padding: 6px 4px 14px; cursor: pointer; }
.brand .logo { width: 26px; height: 26px; border-radius: 9px; }
.brand span { font-family: var(--display); font-weight: 700; font-size: 16px; color: var(--strong); white-space: nowrap; }
.task-btn { border-radius: var(--radius); padding: 9px 14px; margin: 0 2px 14px; white-space: nowrap; }
.nav { display: flex; flex-direction: column; gap: 3px; }
.nav-item { display: flex; align-items: center; gap: 10px; padding: 8px 12px; border: none; background: none; border-radius: 10px; color: var(--muted); font-weight: 400; font-size: 14px; text-align: left; }
.nav-item:hover { color: var(--text); background: none; }
.nav-item.on { background: var(--panel-2); color: var(--strong); font-weight: 600; }
.sessions-head { display: flex; align-items: center; justify-content: space-between; padding: 16px 12px 8px; cursor: pointer; }
.sessions-head span:first-child { font-size: 10px; letter-spacing: 0.12em; color: #6B665E; font-weight: 700; }
.sessions-head:hover span:first-child, .sessions-head.on span:first-child { color: var(--text); }
.wait-badge { font-family: var(--mono); font-size: 11px; background: #33302a; color: var(--warn); padding: 1px 7px; border-radius: 9px; }
.side-sessions { flex: 1; min-height: 0; overflow-y: auto; }
.side-foot { padding: 10px 12px 2px; font-size: 11px; color: var(--faint); white-space: nowrap; }
.err { color: var(--danger); padding: 6px 12px; font: 12px var(--mono); }

.main { flex: 1; display: flex; flex-direction: column; min-width: 0; }
.topbar { height: 56px; flex-shrink: 0; border-bottom: 1px solid var(--border); display: flex; align-items: center; gap: 12px; padding: 0 24px; }
.search { flex: 1; max-width: 400px; display: flex; align-items: center; gap: 8px; background: var(--hover); border: 1px solid var(--border-2); border-radius: var(--radius); padding: 0 12px; color: var(--muted-3); }
.search input { flex: 1; background: none; border: none; padding: 8px 0; font-size: 14px; }
.search input:focus { outline: none; }
.search:focus-within { border-color: var(--accent); }
.kbd { font-family: var(--mono); font-size: 11px; border: 1px solid var(--border-3); border-radius: 5px; padding: 0 5px; }
.spacer { flex: 1; }
.icon-btn { width: 34px; height: 34px; border-radius: 10px; border: none; background: none; display: flex; align-items: center; justify-content: center; color: var(--muted); font-size: 16px; padding: 0; }
.icon-btn:hover, .icon-btn.on { background: var(--panel-2); color: var(--text); }
.avatar { width: 34px; height: 34px; border-radius: 50%; background: #3d3a33; display: flex; align-items: center; justify-content: center; font-size: 12px; font-weight: 700; color: #d6d1c8; cursor: pointer; border: 2px solid var(--border); flex-shrink: 0; }
.avatar:hover { border-color: var(--accent); }
.content { display: flex; flex: 1; min-width: 0; min-height: 0; }
.page { display: flex; flex: 1; min-width: 0; }

@media (max-width: 760px) {
  .shell { flex-direction: column; }
  .side { width: 100%; border-right: 0; border-bottom: 1px solid var(--border); max-height: 45%; }
  .search { max-width: none; }
}
</style>
