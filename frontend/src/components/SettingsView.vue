<script setup>
import { computed, ref } from 'vue'
import { api, auth, config } from '../api.js'
import AccountDialog from './AccountDialog.vue'
import CredentialsDialog from './CredentialsDialog.vue'
import SettingsDialog from './SettingsDialog.vue'
import AdminView from './AdminView.vue'
import AdminLimitsView from './AdminLimitsView.vue'
import McpServersPane from './McpServersPane.vue'
import SkillsPane from './SkillsPane.vue'
import GroupsPane from './GroupsPane.vue'
import WebhooksPane from './WebhooksPane.vue'
import { initials } from '../lib/text.js'

defineEmits(['close'])
const props = defineProps({
  initialTab: { type: String, default: 'credentials' },
  isAdmin: { type: Boolean, default: false }
})

const personalTabs = computed(() => [
  { key: 'profile', label: 'Profile' },
  { key: 'account', label: 'Connected accounts', show: () => config.gitEnabled },
  { key: 'credentials', label: 'Credentials' },
  { key: 'mcp', label: 'MCP servers' },
  { key: 'skills', label: 'Skills' },
  { key: 'notifications', label: 'Notifications' },
  { key: 'tokens', label: 'API tokens' },
  { key: 'webhooks', label: 'Webhooks' }
].filter(t => !t.show || t.show()))

const adminTabs = [
  { key: 'users', label: 'Users & seats' },
  { key: 'org-mcp', label: 'Org MCP catalog' },
  { key: 'limits', label: 'Usage limits & groups' },
  { key: 'groups', label: 'Skill publishing' },
  { key: 'billing', label: 'Billing & invoices' },
  { key: 'license', label: 'License' }
]
const active = ref(props.initialTab)

// GDPR account deletion: type-to-confirm, no popup (inline card).
const deleteConfirm = ref('')
const deleteBusy = ref(false)
const deleteError = ref('')
const deleteArmed = computed(() => deleteConfirm.value === auth.user)

async function deleteAccount() {
  if (!deleteArmed.value || deleteBusy.value) return
  deleteBusy.value = true
  deleteError.value = ''
  try {
    await api.deleteAccount(auth.user)
    // The account is gone — end the session. Without auth, just reload into dev mode.
    if (auth.enabled) auth.logout()
    else location.assign('/')
  } catch (e) {
    deleteError.value = String(e.message || e)
    deleteBusy.value = false
  }
}
</script>

<template>
  <div class="settings-page">
    <nav class="subnav">
      <div class="head">Settings</div>
      <div class="section">PERSONAL</div>
      <button v-for="t in personalTabs" :key="t.key" class="tab" :class="{ on: active === t.key }" @click="active = t.key">{{ t.label }}</button>
      <template v-if="isAdmin">
        <div class="section admin">ADMIN</div>
        <button v-for="t in adminTabs" :key="t.key" class="tab" :class="{ on: active === t.key }" @click="active = t.key">{{ t.label }}</button>
      </template>
    </nav>
    <div class="body">
      <div v-if="active === 'profile'" class="pane">
        <h3>Profile</h3>
        <div class="card profile-card">
          <div class="prow">
            <span class="avatar">{{ initials(auth.displayName || auth.user) }}</span>
            <div>
              <div class="pname">{{ auth.displayName || auth.user }}</div>
              <div class="pmeta">{{ auth.email || auth.user }}</div>
            </div>
          </div>
          <div class="prow border">
            <div class="grow">
              <div class="plabel">Signed in as</div>
              <div class="pvalue">{{ auth.user }}</div>
            </div>
            <button v-if="auth.enabled" class="danger" @click="auth.logout()">Sign out</button>
            <span v-else class="pmeta">Local development mode — authentication is disabled.</span>
          </div>
        </div>
        <div class="card danger-card" data-danger-zone>
          <div class="dz-title">Danger zone</div>
          <p class="pmeta">Deleting your account removes <b>all</b> of your data permanently:
          sessions and their files, credentials, git connections, skills, MCP servers,
          usage history and chat links. This cannot be undone. Your sign-in identity at the
          identity provider is not affected — signing in again starts an empty account.</p>
          <label class="dz-label" for="delete-confirm">Type your username <b>{{ auth.user }}</b> to confirm</label>
          <div class="dz-row">
            <input id="delete-confirm" v-model="deleteConfirm" data-delete-confirm
              :placeholder="auth.user" autocomplete="off" />
            <button class="danger" data-delete-account :disabled="!deleteArmed || deleteBusy"
              @click="deleteAccount">{{ deleteBusy ? 'Deleting…' : 'Delete account permanently' }}</button>
          </div>
          <p v-if="deleteError" class="dz-err" data-delete-error>{{ deleteError }}</p>
        </div>
      </div>
      <AccountDialog v-else-if="active === 'account'" embedded />
      <CredentialsDialog v-else-if="active === 'credentials'" embedded @accounts="active = 'account'" />
      <McpServersPane v-else-if="active === 'mcp'" :is-admin="isAdmin" mode="personal" />
      <SkillsPane v-else-if="active === 'skills'" :is-admin="isAdmin" />
      <GroupsPane v-else-if="active === 'groups'" />
      <SettingsDialog v-else-if="active === 'notifications'" embedded section="notifications" />
      <SettingsDialog v-else-if="active === 'tokens'" embedded section="tokens" />
      <WebhooksPane v-else-if="active === 'webhooks'" />
      <AdminView v-else-if="active === 'users'" embedded section="seats" />
      <McpServersPane v-else-if="active === 'org-mcp'" :is-admin="true" mode="org" />
      <AdminLimitsView v-else-if="active === 'limits'" embedded />
      <AdminView v-else-if="active === 'billing'" embedded section="billing" />
      <AdminView v-else-if="active === 'license'" embedded section="license" />
    </div>
  </div>
</template>

<style scoped>
.settings-page { flex: 1; display: flex; min-width: 0; min-height: 0; background: var(--bg); }
.subnav { width: 220px; flex-shrink: 0; border-right: 1px solid var(--border); padding: 24px 12px; display: flex; flex-direction: column; gap: 2px; overflow-y: auto; }
.head { font-family: var(--display); font-size: 20px; font-weight: 700; padding: 0 10px 14px; color: var(--strong); }
.section { padding: 4px 10px 6px; font-size: 10px; letter-spacing: 0.12em; color: #6B665E; font-weight: 700; }
.section.admin { padding-top: 16px; }
.tab { padding: 7px 10px; border-radius: 9px; font-size: 13px; border: none; background: none; color: var(--muted); font-weight: 400; text-align: left; }
.tab:hover { color: var(--text); background: none; }
.tab.on { background: var(--panel-2); color: var(--strong); font-weight: 600; }
.body { flex: 1; min-width: 0; overflow-y: auto; display: flex; flex-direction: column; }
.pane { max-width: 560px; padding: 26px 30px; }
.pane h3 { font-size: 22px; margin: 0 0 16px; }
.profile-card { padding: 18px 20px; }
.prow { display: flex; align-items: center; gap: 14px; }
.prow.border { border-top: 1px solid var(--border); margin-top: 16px; padding-top: 16px; }
.grow { flex: 1; }
.avatar { width: 44px; height: 44px; border-radius: 50%; background: #3d3a33; display: flex; align-items: center; justify-content: center; font-size: 15px; font-weight: 700; color: #d6d1c8; flex-shrink: 0; }
.pname { font-weight: 700; color: var(--strong); }
.pmeta { font-size: 13px; color: var(--muted-2); margin-top: 2px; }
.plabel { font-size: 12px; color: var(--muted-2); margin-bottom: 4px; }
.pvalue { font-family: var(--mono); font-size: 13px; }
.danger-card { margin-top: 18px; padding: 18px 20px; border-color: var(--danger); }
.dz-title { font-weight: 700; color: var(--danger); margin-bottom: 6px; }
.dz-label { display: block; font-size: 12px; color: var(--muted-2); margin: 12px 0 6px; }
.dz-row { display: flex; gap: 10px; align-items: center; }
.dz-row input { flex: 1; font-family: var(--mono); }
.dz-err { color: var(--danger); font-family: var(--mono); font-size: 12px; margin: 8px 0 0; }
@media (max-width: 760px) { .settings-page { flex-direction: column; } .subnav { width: 100%; flex-direction: row; flex-wrap: wrap; border-right: 0; border-bottom: 1px solid var(--border); } .head { flex-basis: 100%; } }
</style>
