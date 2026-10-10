<script setup>
import { computed, watch } from 'vue'
import {
  accountOptionLabel, accountsFor, agentOptions, authOptions, credentialReadiness, defaultAccountId,
  needsOpenClawApiKeySource, openClawApiKeySourceOptions
} from '../lib/agent.js'

const props = defineProps({
  agent: { type: String, required: true },
  authMode: { type: String, required: true },
  openClawApiKeySource: { type: String, default: 'Anthropic' },
  mode: { type: String, required: true },
  legacyAuthMode: { type: String, default: null },
  credentialStatus: { type: Object, default: () => ({}) },
  // Stored provider logins keyed by agent name, as the accounts endpoint returns them.
  accounts: { type: Object, default: () => ({}) },
  credentialId: { type: String, default: '' },
  options: { type: Array, default: null }
})
const emit = defineEmits(['update:agent', 'update:authMode', 'update:openClawApiKeySource', 'update:credentialId'])
const visibleAgents = computed(() => props.options || agentOptions)
const billingOptions = computed(() => authOptions(props.agent, props.legacyAuthMode))
const showOpenClawSource = computed(() => needsOpenClawApiKeySource(props.agent, props.authMode))
const readiness = computed(() =>
  credentialReadiness(props.agent, props.authMode, props.mode, props.credentialStatus, props.openClawApiKeySource))
const providerAccounts = computed(() => accountsFor(props.accounts, props.agent))
// Shown from the first login, with the default preselected. It used to wait for a second one,
// which left the dialog silent about *which* login the session would run on — and once an API
// token can be restricted to some accounts (docs/credential-scopes.md), "the default" is no
// longer something a person can take for granted.
const showAccounts = computed(() => props.authMode !== 'ApiKey' && providerAccounts.value.length >= 1)
// The listing always carries every agent's key, so an empty object is "not loaded yet" (or an
// older backend) rather than "no accounts". Until it arrives a pinned id must be left alone:
// resetting it against an empty list would wipe the pin of a session being edited or copied.
const accountsLoaded = computed(() => Object.keys(props.accounts || {}).length > 0)

watch([() => props.agent, providerAccounts, showAccounts, () => props.credentialId], ([agent], [previousAgent] = []) => {
  // An account belongs to one provider, so a pin never survives an agent change — loaded or not.
  const agentChanged = previousAgent !== undefined && agent !== previousAgent
  if (!agentChanged && !accountsLoaded.value) return
  const known = !agentChanged && providerAccounts.value.some(account => account.id === props.credentialId)
  if (known) return
  const next = showAccounts.value ? defaultAccountId(providerAccounts.value) : ''
  if (next !== props.credentialId) emit('update:credentialId', next)
}, { immediate: true })

function chooseAgent(agent) {
  emit('update:agent', agent)
  if (props.authMode === 'Auto' && agent !== 'Claude') emit('update:authMode', 'Subscription')
}
</script>

<template>
  <div class="agent-card" data-agent-card>
    <div class="decision-group">
      <div class="decision-label">Agent</div>
      <div class="chips-box" role="group" aria-label="Agent">
        <button v-for="option in visibleAgents" :key="option.value" type="button" class="chip"
          :class="{ on: agent === option.value }" :aria-pressed="agent === option.value"
          :data-agent-option="option.value" @click="chooseAgent(option.value)">{{ option.label }}</button>
      </div>
      <small>{{ visibleAgents.find(option => option.value === agent)?.hint }}</small>
    </div>
    <div class="decision-group">
      <div class="decision-label">Billing</div>
      <div class="chips-box" role="group" aria-label="Billing source">
        <button v-for="option in billingOptions" :key="option.value" type="button" class="chip"
          :class="{ on: authMode === option.value }" :aria-pressed="authMode === option.value"
          :disabled="option.value === 'Auto'"
          :data-auth-option="option.value" @click="$emit('update:authMode', option.value)">{{ option.label }}</button>
      </div>
      <small>{{ billingOptions.find(option => option.value === authMode)?.hint }}</small>
    </div>
    <div v-if="showOpenClawSource" class="decision-group source-group" data-openclaw-source>
      <div class="decision-label">API key source</div>
      <div class="chips-box" role="group" aria-label="OpenClaw API key source">
        <button v-for="option in openClawApiKeySourceOptions" :key="option.value" type="button" class="chip"
          :class="{ on: openClawApiKeySource === option.value }" :aria-pressed="openClawApiKeySource === option.value"
          :data-openclaw-source-option="option.value"
          @click="$emit('update:openClawApiKeySource', option.value)">{{ option.label }}</button>
      </div>
      <small>{{ openClawApiKeySourceOptions.find(option => option.value === openClawApiKeySource)?.hint }}</small>
    </div>
    <div v-if="showAccounts" class="decision-group source-group" data-account-choice>
      <div class="decision-label">Account</div>
      <select data-account-select :value="credentialId" aria-label="Provider account"
        @change="$emit('update:credentialId', $event.target.value)">
        <option v-for="account in providerAccounts" :key="account.id" :value="account.id" :data-account-option="account.id">
          {{ accountOptionLabel(account) }}{{ account.isDefault ? ' (default)' : '' }}
        </option>
      </select>
      <small>Which stored {{ agent }} login this session uses. A running session can be switched from its header.</small>
    </div>
    <p class="readiness" :class="{ ready: readiness.ready }" data-readiness aria-live="polite">{{ readiness.text }}</p>
  </div>
</template>

<style scoped>
.agent-card {
  display: grid; grid-template-columns: minmax(0, 1fr) minmax(0, 1fr); gap: 14px 18px;
  padding: 14px; margin: 2px 0 14px; border: 1px solid var(--border-2); border-radius: var(--radius-lg); background: var(--input);
}
.decision-label { margin-bottom: 7px; color: var(--muted-2); font-size: 12px; }
.chips-box { display: inline-flex; max-width: 100%; gap: 2px; padding: 3px; border: 1px solid var(--border-2); border-radius: var(--radius); background: var(--panel); }
.chip { padding: 6px 12px; border: none; border-radius: 8px; background: none; color: var(--muted-3); font-size: 12px; font-weight: 700; }
.chip:hover { color: var(--text); }
.chip.on { background: var(--border-2); color: var(--strong); }
small { display: block; margin-top: 5px; color: var(--muted-3); font-size: 11px; }
.source-group { grid-column: 1 / -1; }
.source-group select { max-width: 100%; }
.readiness { grid-column: 1 / -1; margin: 0; padding-top: 10px; border-top: 1px solid var(--border); color: var(--warn); font-size: 12px; line-height: 1.45; }
.readiness.ready { color: var(--ok); }
@media (max-width: 600px) {
  .agent-card { grid-template-columns: 1fr; }
  .source-group, .readiness { grid-column: 1; }
  .chips-box { display: flex; }
  .chip { flex: 1; }
}
</style>
