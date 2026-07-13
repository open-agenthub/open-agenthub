<script setup>
import { ref, computed, onMounted, watch } from 'vue'
import { api, auth, config } from './api.js'
import SessionList from './components/SessionList.vue'
import TerminalView from './components/TerminalView.vue'
import NewSessionDialog from './components/NewSessionDialog.vue'
import CredentialsDialog from './components/CredentialsDialog.vue'
import SettingsDialog from './components/SettingsDialog.vue'
import UsageDialog from './components/UsageDialog.vue'
import EditSessionDialog from './components/EditSessionDialog.vue'
import AccountDialog from './components/AccountDialog.vue'
import { sessionMatches } from './lib/text.js'

const sessions = ref([])
const activeId = ref(null)
const showNew = ref(false)
const showCreds = ref(false)
const showSettings = ref(false)
const showUsage = ref(false)
const showAccount = ref(false)
const editId = ref(null)
const error = ref('')
const search = ref('')
const gitEnabled = config

const filteredSessions = computed(() => sessions.value.filter(s => sessionMatches(s, search.value)))

const activeSession = computed(() => sessions.value.find(s => s.id === activeId.value) || null)
const editSession = computed(() => sessions.value.find(s => s.id === editId.value) || null)

// Auth enabled but no valid login: show the login page instead of the app.
// State changes always go through a full-page redirect, so the state at mount time is sufficient.
const needsLogin = auth.enabled && !auth.isAuthenticated

async function refresh() {
  try { sessions.value = await api.listSessions() }
  catch (e) { error.value = String(e.message || e) }
}

async function onCreated(session) { showNew.value = false; await refresh(); activeId.value = session.id }

async function onUpdated() { editId.value = null; await refresh() }

async function resume(id) {
  await api.resumeSession(id)
  await refresh()
  activeId.value = id
}

async function pause(id) {
  await api.pauseSession(id)
  await refresh()
}

async function remove(id) {
  if (!confirm('Really delete this session? (S3 artifacts are kept)')) return
  await api.deleteSession(id)
  if (activeId.value === id) activeId.value = null
  await refresh()
}

// Deep-linkable sessions: the active session id lives in the URL path (/s/{id}),
// so a session can be linked and survives reloads / back-forward navigation.
function idFromLocation() {
  const m = location.pathname.match(/^\/s\/([^/]+)/)
  if (m) return decodeURIComponent(m[1])
  return new URLSearchParams(location.search).get('session') // legacy ?session=
}

let syncingFromHistory = false // suppress the watch while reacting to back/forward

watch(activeId, (id) => {
  if (syncingFromHistory) return
  const target = id ? `/s/${encodeURIComponent(id)}` : '/'
  if (location.pathname + location.search !== target) history.pushState({ id }, '', target)
})

onMounted(() => {
  if (needsLogin) return
  if (location.pathname.startsWith('/account')) showAccount.value = true
  const fromUrl = idFromLocation()
  if (fromUrl) activeId.value = fromUrl
  window.addEventListener('popstate', () => {
    syncingFromHistory = true
    activeId.value = idFromLocation()
    syncingFromHistory = false
  })
  refresh(); setInterval(refresh, 5000)
})
</script>

<template>
  <div v-if="needsLogin" class="login">
    <div class="login-card">
      <div class="brand">
        <svg class="logo" width="30" height="22" viewBox="0 0 40 30" fill="none" aria-hidden="true">
          <g fill="var(--muted)">
            <circle cx="8.5" cy="8" r="2.9" />
            <path d="M3.9 16 a4.6 4.6 0 0 1 9.2 0 Z" />
            <rect x="4.4" y="13.8" width="8.2" height="5.6" rx="1" stroke="var(--bg)" stroke-width="1.4" />
            <path d="M2.9 21.2 L14.1 21.2 L15.1 23.4 L1.9 23.4 Z" />
          </g>
          <g fill="var(--muted)">
            <circle cx="31.5" cy="8" r="2.9" />
            <path d="M26.9 16 a4.6 4.6 0 0 1 9.2 0 Z" />
            <rect x="27.4" y="13.8" width="8.2" height="5.6" rx="1" stroke="var(--bg)" stroke-width="1.4" />
            <path d="M25.9 21.2 L37.1 21.2 L38.1 23.4 L24.9 23.4 Z" />
          </g>
          <g fill="var(--accent)">
            <circle cx="20" cy="7" r="3.3" />
            <path d="M14.8 16.5 a5.2 5.2 0 0 1 10.4 0 Z" />
            <rect x="15.2" y="13.6" width="9.6" height="6.4" rx="1.2" stroke="var(--bg)" stroke-width="1.6" />
            <path d="M13.4 21.8 L26.6 21.8 L27.8 24.2 L12.2 24.2 Z" />
          </g>
        </svg>
        <span class="name">Open AgentHub</span>
        <span class="sub">Agent Control</span>
      </div>
      <p class="login-hint">Please sign in via the configured OIDC provider to manage your agent sessions.</p>
      <button class="primary" @click="auth.login()">Sign in</button>
    </div>
  </div>

  <div v-else class="shell" :class="{ 'detail-open': activeSession }">
    <header class="topbar">
      <div class="brand">
        <svg class="logo" width="30" height="22" viewBox="0 0 40 30" fill="none" aria-hidden="true">
          <g fill="var(--muted)">
            <circle cx="8.5" cy="8" r="2.9" />
            <path d="M3.9 16 a4.6 4.6 0 0 1 9.2 0 Z" />
            <rect x="4.4" y="13.8" width="8.2" height="5.6" rx="1" stroke="var(--bg)" stroke-width="1.4" />
            <path d="M2.9 21.2 L14.1 21.2 L15.1 23.4 L1.9 23.4 Z" />
          </g>
          <g fill="var(--muted)">
            <circle cx="31.5" cy="8" r="2.9" />
            <path d="M26.9 16 a4.6 4.6 0 0 1 9.2 0 Z" />
            <rect x="27.4" y="13.8" width="8.2" height="5.6" rx="1" stroke="var(--bg)" stroke-width="1.4" />
            <path d="M25.9 21.2 L37.1 21.2 L38.1 23.4 L24.9 23.4 Z" />
          </g>
          <g fill="var(--accent)">
            <circle cx="20" cy="7" r="3.3" />
            <path d="M14.8 16.5 a5.2 5.2 0 0 1 10.4 0 Z" />
            <rect x="15.2" y="13.6" width="9.6" height="6.4" rx="1.2" stroke="var(--bg)" stroke-width="1.6" />
            <path d="M13.4 21.8 L26.6 21.8 L27.8 24.2 L12.2 24.2 Z" />
          </g>
        </svg>
        <span class="name">Open AgentHub</span>
        <span class="sub">Agent Control</span>
      </div>
      <div class="actions">
        <span class="user">{{ auth.user }}</span>
        <button v-if="gitEnabled.gitEnabled" @click="showAccount = true">Account</button>
        <button @click="showUsage = true">Usage</button>
        <button @click="showCreds = true">Credentials</button>
        <button @click="showSettings = true">Settings</button>
        <button v-if="auth.enabled" @click="auth.logout()">Sign out</button>
      </div>
    </header>

    <main class="layout">
      <aside class="sidebar">
        <div class="sidebar-head">
          <h2>Sessions</h2>
          <button class="primary" @click="showNew = true">New Session</button>
        </div>
        <input v-model="search" class="session-search" placeholder="Search sessions…"
               autocapitalize="off" autocomplete="off" spellcheck="false" />
        <p v-if="error" class="err">{{ error }}</p>
        <SessionList :sessions="filteredSessions" :active="activeId"
          @select="activeId = $event" @remove="remove" @resume="resume" @pause="pause" @edit="editId = $event" />
      </aside>

      <section class="content">
        <TerminalView v-if="activeSession" :session="activeSession" :key="activeId"
          @back="activeId = null" @resume="resume" @pause="pause" />
        <div v-else class="empty">
          <p>No session selected.</p>
          <p class="hint">Select a session on the left or start a new one. You can watch, reply, let agents work autonomously or on a schedule, and resume finished sessions.</p>
        </div>
      </section>
    </main>

    <NewSessionDialog v-if="showNew" @close="showNew = false" @created="onCreated" />
    <CredentialsDialog v-if="showCreds" @close="showCreds = false" />
    <SettingsDialog v-if="showSettings" @close="showSettings = false" />
    <UsageDialog v-if="showUsage" @close="showUsage = false" />
    <AccountDialog v-if="showAccount" @close="showAccount = false" />
    <EditSessionDialog v-if="editSession" :session="editSession" :key="editId"
      @close="editId = null" @updated="onUpdated" />
  </div>
</template>

<style scoped>
.login { height: 100%; display: flex; align-items: center; justify-content: center; padding: 24px; }
.login-card { background: var(--panel); border: 1px solid var(--border); border-radius: var(--radius); padding: 32px 28px; max-width: 360px; width: 100%; display: flex; flex-direction: column; gap: 18px; align-items: flex-start; }
.login-hint { margin: 0; color: var(--muted); font-size: 13px; line-height: 1.5; }
.shell { display: flex; flex-direction: column; height: 100%; }
.topbar { display: flex; align-items: center; justify-content: space-between; padding: 12px 18px; border-bottom: 1px solid var(--border); background: var(--panel); padding-top: max(12px, env(safe-area-inset-top)); }
.brand { display: flex; align-items: baseline; gap: 10px; }
.brand .name { font-weight: 700; letter-spacing: .02em; }
.brand .sub { color: var(--muted); font-size: 12px; font-family: var(--mono); }
.brand .logo { align-self: center; display: block; flex-shrink: 0; }
.actions { display: flex; align-items: center; gap: 8px; }
.actions .user { color: var(--muted); font-family: var(--mono); font-size: 13px; }
.layout { flex: 1; display: flex; min-height: 0; }
.sidebar { width: 340px; border-right: 1px solid var(--border); background: var(--panel); display: flex; flex-direction: column; min-height: 0; }
.sidebar-head { display: flex; align-items: center; justify-content: space-between; padding: 16px; }
.sidebar-head h2 { margin: 0; font-size: 15px; }
.session-search { width: calc(100% - 32px); box-sizing: border-box; margin: 0 16px 10px; padding: 7px 10px; font-size: 13px; }
.content { flex: 1; display: flex; min-width: 0; }
.empty { margin: auto; max-width: 380px; text-align: center; color: var(--muted); padding: 24px; }
.empty .hint { font-size: 13px; line-height: 1.5; margin-top: 8px; }
.err { color: var(--danger); font-size: 12px; padding: 0 16px; font-family: var(--mono); }
@media (max-width: 760px) {
  .layout { flex-direction: column; }
  .sidebar { width: 100%; border-right: none; }
  .content { display: none; }
  .detail-open .sidebar { display: none; }
  .detail-open .content { display: flex; }
  .actions .user { display: none; }
}
</style>
