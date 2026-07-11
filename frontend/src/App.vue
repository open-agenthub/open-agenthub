<script setup>
import { ref, computed, onMounted } from 'vue'
import { api, auth } from './api.js'
import SessionList from './components/SessionList.vue'
import TerminalView from './components/TerminalView.vue'
import NewSessionDialog from './components/NewSessionDialog.vue'
import CredentialsDialog from './components/CredentialsDialog.vue'

const sessions = ref([])
const activeId = ref(null)
const showNew = ref(false)
const showCreds = ref(false)
const error = ref('')

const activeSession = computed(() => sessions.value.find(s => s.id === activeId.value) || null)

// Auth enabled but no valid login: show the login page instead of the app.
// State changes always go through a full-page redirect, so the state at mount time is sufficient.
const needsLogin = auth.enabled && !auth.isAuthenticated

async function refresh() {
  try { sessions.value = await api.listSessions() }
  catch (e) { error.value = String(e.message || e) }
}

async function onCreated(session) { showNew.value = false; await refresh(); activeId.value = session.id }

async function resume(id) {
  await api.resumeSession(id)
  await refresh()
  activeId.value = id
}

async function remove(id) {
  if (!confirm('Really delete this session? (S3 artifacts are kept)')) return
  await api.deleteSession(id)
  if (activeId.value === id) activeId.value = null
  await refresh()
}

onMounted(() => {
  if (needsLogin) return
  const fromUrl = new URLSearchParams(location.search).get('session')
  if (fromUrl) activeId.value = fromUrl
  refresh(); setInterval(refresh, 5000)
})
</script>

<template>
  <div v-if="needsLogin" class="login">
    <div class="login-card">
      <div class="brand">
        <span class="pulse"></span>
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
        <span class="pulse"></span>
        <span class="name">Open AgentHub</span>
        <span class="sub">Agent Control</span>
      </div>
      <div class="actions">
        <span class="user">{{ auth.user }}</span>
        <button @click="showCreds = true">Credentials</button>
        <button v-if="auth.enabled" @click="auth.logout()">Sign out</button>
      </div>
    </header>

    <main class="layout">
      <aside class="sidebar">
        <div class="sidebar-head">
          <h2>Sessions</h2>
          <button class="primary" @click="showNew = true">New Session</button>
        </div>
        <p v-if="error" class="err">{{ error }}</p>
        <SessionList :sessions="sessions" :active="activeId"
          @select="activeId = $event" @remove="remove" @resume="resume" />
      </aside>

      <section class="content">
        <TerminalView v-if="activeSession" :session="activeSession" :key="activeId"
          @back="activeId = null" @resume="resume" />
        <div v-else class="empty">
          <p>No session selected.</p>
          <p class="hint">Select a session on the left or start a new one. You can watch, reply, let agents work autonomously or on a schedule, and resume finished sessions.</p>
        </div>
      </section>
    </main>

    <NewSessionDialog v-if="showNew" @close="showNew = false" @created="onCreated" />
    <CredentialsDialog v-if="showCreds" @close="showCreds = false" />
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
.pulse { width: 9px; height: 9px; border-radius: 50%; background: var(--accent); align-self: center; box-shadow: 0 0 0 0 var(--accent); animation: pulse 2.4s infinite; }
@keyframes pulse { 0% { box-shadow: 0 0 0 0 rgba(232,162,74,.5); } 70% { box-shadow: 0 0 0 8px rgba(232,162,74,0); } 100% { box-shadow: 0 0 0 0 rgba(232,162,74,0); } }
.actions { display: flex; align-items: center; gap: 8px; }
.actions .user { color: var(--muted); font-family: var(--mono); font-size: 13px; }
.layout { flex: 1; display: flex; min-height: 0; }
.sidebar { width: 340px; border-right: 1px solid var(--border); background: var(--panel); display: flex; flex-direction: column; min-height: 0; }
.sidebar-head { display: flex; align-items: center; justify-content: space-between; padding: 16px; }
.sidebar-head h2 { margin: 0; font-size: 15px; }
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
