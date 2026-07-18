<script setup>
import { computed, ref } from 'vue'
import { auth, config } from '../api.js'
import AccountDialog from './AccountDialog.vue'
import CredentialsDialog from './CredentialsDialog.vue'
import SettingsDialog from './SettingsDialog.vue'
import AdminView from './AdminView.vue'
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
  { key: 'notifications', label: 'Notifications' },
  { key: 'tokens', label: 'API tokens' }
].filter(t => !t.show || t.show()))

const adminTabs = [
  { key: 'users', label: 'Users & seats' },
  { key: 'billing', label: 'Billing & invoices' },
  { key: 'license', label: 'License' }
]
const active = ref(props.initialTab)
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
      </div>
      <AccountDialog v-else-if="active === 'account'" embedded />
      <CredentialsDialog v-else-if="active === 'credentials'" embedded />
      <SettingsDialog v-else-if="active === 'notifications'" embedded section="notifications" />
      <SettingsDialog v-else-if="active === 'tokens'" embedded section="tokens" />
      <AdminView v-else-if="active === 'users'" embedded section="seats" />
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
@media (max-width: 760px) { .settings-page { flex-direction: column; } .subnav { width: 100%; flex-direction: row; flex-wrap: wrap; border-right: 0; border-bottom: 1px solid var(--border); } .head { flex-basis: 100%; } }
</style>
