<script setup>
import { computed } from 'vue'
import { agentOptions, authOptions, credentialReadiness } from '../lib/agent.js'

const props = defineProps({
  agent: { type: String, required: true },
  authMode: { type: String, required: true },
  mode: { type: String, required: true },
  legacyAuthMode: { type: String, default: null },
  credentialStatus: { type: Object, default: () => ({}) }
})
const emit = defineEmits(['update:agent', 'update:authMode'])
const billingOptions = computed(() => authOptions(props.agent, props.legacyAuthMode))
const readiness = computed(() => credentialReadiness(props.agent, props.authMode, props.mode, props.credentialStatus))

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
        <button v-for="option in agentOptions" :key="option.value" type="button" class="chip"
          :class="{ on: agent === option.value }" :aria-pressed="agent === option.value"
          :data-agent-option="option.value" @click="chooseAgent(option.value)">{{ option.label }}</button>
      </div>
      <small>{{ agentOptions.find(option => option.value === agent)?.hint }}</small>
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
.readiness { grid-column: 1 / -1; margin: 0; padding-top: 10px; border-top: 1px solid var(--border); color: var(--warn); font-size: 12px; line-height: 1.45; }
.readiness.ready { color: var(--ok); }
@media (max-width: 600px) {
  .agent-card { grid-template-columns: 1fr; }
  .readiness { grid-column: 1; }
  .chips-box { display: flex; }
  .chip { flex: 1; }
}
</style>
