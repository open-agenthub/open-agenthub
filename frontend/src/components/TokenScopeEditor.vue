<script setup>
// The inline "Restrict to credentials" card of the API-token settings: one checkbox per stored
// provider account, one per git token, and a switch for API-key sessions. The draft it edits is
// an allow list (docs/credential-scopes.md); whatever is left unticked is not allowed.
import { computed } from 'vue'
import { accountOptionLabel } from '../lib/agent.js'
import { AGENTS, draftAllowsAnything } from '../lib/token-scope.js'

const props = defineProps({
  accounts: { type: Object, default: () => ({}) },
  pats: { type: Array, default: () => [] },
  modelValue: { type: Object, required: true }
})
const emit = defineEmits(['update:modelValue'])

const agentsWithAccounts = computed(() =>
  AGENTS.filter(agent => Array.isArray(props.accounts?.[agent]) && props.accounts[agent].length))
const allowsAnything = computed(() => draftAllowsAnything(props.modelValue))
const kindLabel = kind => (kind === 'github' ? 'GitHub' : 'GitLab')

function toggleAccount(agent, id) {
  const current = props.modelValue.providerAccounts[agent] || []
  const next = current.includes(id) ? current.filter(item => item !== id) : [...current, id]
  emit('update:modelValue', {
    ...props.modelValue,
    providerAccounts: { ...props.modelValue.providerAccounts, [agent]: next }
  })
}

function togglePat(id) {
  const current = props.modelValue.gitPats || []
  emit('update:modelValue', {
    ...props.modelValue,
    gitPats: current.includes(id) ? current.filter(item => item !== id) : [...current, id]
  })
}

function toggleApiKeys(event) {
  emit('update:modelValue', { ...props.modelValue, apiKeys: event.target.checked })
}
</script>

<template>
  <div class="scope" data-token-scope-editor>
    <p class="note">Sessions created with this token may only use what is ticked here. Anything left unticked is not available to it.</p>
    <div v-for="agent in agentsWithAccounts" :key="agent" class="group" :data-scope-agent="agent">
      <div class="group-label">{{ agent }} logins</div>
      <label v-for="account in accounts[agent]" :key="account.id" class="check">
        <input type="checkbox" :checked="(modelValue.providerAccounts[agent] || []).includes(account.id)"
          :data-scope-account="`${agent}:${account.id}`" @change="toggleAccount(agent, account.id)" />
        <span>{{ accountOptionLabel(account) }}<span v-if="account.isDefault" class="dim"> (default)</span></span>
      </label>
    </div>
    <p v-if="!agentsWithAccounts.length" class="note dim" data-scope-no-accounts>No provider login is stored yet — a token restricted now can only start API-key sessions.</p>
    <div v-if="pats.length" class="group" data-scope-pats>
      <div class="group-label">Git tokens</div>
      <label v-for="pat in pats" :key="pat.id" class="check">
        <input type="checkbox" :checked="(modelValue.gitPats || []).includes(pat.id)" :data-scope-pat="pat.id" @change="togglePat(pat.id)" />
        <span>{{ pat.host }} <span class="kind">{{ kindLabel(pat.kind) }}</span></span>
      </label>
    </div>
    <label class="check">
      <input type="checkbox" :checked="!!modelValue.apiKeys" data-scope-api-keys @change="toggleApiKeys" />
      <span>API keys <span class="dim">— sessions billed to a stored API key</span></span>
    </label>
    <p v-if="!allowsAnything" class="warn" data-scope-empty>Nothing is ticked: a session created with this token would be refused.</p>
  </div>
</template>

<style scoped>
.scope { margin: 10px 0 14px; padding: 12px 14px; border: 1px solid var(--border); border-radius: 10px; }
.note { margin: 0 0 10px; color: var(--muted); font-size: 12px; line-height: 1.5; }
.group { margin-bottom: 10px; }
.group-label { margin-bottom: 4px; color: var(--muted-2); font-size: 12px; }
.check { display: flex; align-items: center; gap: 8px; margin: 3px 0; font-size: 13px; cursor: pointer; }
.check input { width: auto; }
.dim { color: var(--faint); font-weight: 400; }
.kind { display: inline-block; margin-left: 6px; padding: 1px 6px; border: 1px solid var(--border-2); border-radius: 4px; color: var(--muted-3); font-size: 11px; font-weight: 700; }
.warn { margin: 8px 0 0; color: var(--warn); font-size: 12px; }
</style>
