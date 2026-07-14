<script setup>
import { ref } from 'vue'
import { config } from '../api.js'
import AccountDialog from './AccountDialog.vue'
import CredentialsDialog from './CredentialsDialog.vue'
import SettingsDialog from './SettingsDialog.vue'
import UsageDialog from './UsageDialog.vue'

defineEmits(['close'])
const props = defineProps({ initialTab: { type: String, default: 'credentials' } })

const tabs = [
  { key: 'credentials', label: 'Credentials' },
  { key: 'account', label: 'Git accounts', show: () => config.gitEnabled },
  { key: 'notifications', label: 'Notifications & tokens' },
  { key: 'usage', label: 'Usage' }
]
const active = ref(props.initialTab)
</script>

<template>
  <div class="settings-page">
    <div class="bar">
      <button class="back" @click="$emit('close')">‹ Back</button>
      <span class="title">Settings</span>
    </div>
    <nav class="tabs">
      <template v-for="t in tabs" :key="t.key">
        <button v-if="!t.show || t.show()" :class="{ on: active === t.key }" @click="active = t.key">{{ t.label }}</button>
      </template>
    </nav>
    <CredentialsDialog v-if="active === 'credentials'" embedded />
    <AccountDialog v-else-if="active === 'account'" embedded />
    <SettingsDialog v-else-if="active === 'notifications'" embedded />
    <UsageDialog v-else-if="active === 'usage'" embedded />
  </div>
</template>

<style scoped>
.settings-page { flex: 1; display: flex; flex-direction: column; min-width: 0; min-height: 0; background: var(--bg); }
.bar { display: flex; align-items: center; gap: 12px; padding: 8px 14px; border-bottom: 1px solid var(--border); background: var(--panel); }
.bar .title { font-size: 13px; font-weight: 600; }
.back { background: none; border: none; color: var(--muted); }
.back:hover { color: var(--accent); border: none; }
.tabs { display: flex; gap: 4px; flex-wrap: wrap; padding: 10px 14px; border-bottom: 1px solid var(--border); }
.tabs button { font-size: 13px; padding: 5px 12px; color: var(--muted); background: none; border: 1px solid transparent; border-radius: var(--radius); }
.tabs button.on { color: var(--text); border-color: var(--border); background: var(--panel-2); }
.tabs button:hover { color: var(--accent); }
</style>
