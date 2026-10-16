<script setup>
import { computed } from 'vue'
import { formatRemaining, isExpiringSoon } from '../lib/expiry.js'

// One card for the New, Edit and Duplicate dialogs, so the three cannot drift in what they send.
// `modelValue` is the shape autoDeleteForm() produces: { enabled, amount, unit, from }.
const props = defineProps({
  modelValue: { type: Object, required: true },
  // A scheduled session is never attached to, so only the start basis means anything there.
  scheduled: { type: Boolean, default: false },
  // The stored deadline, shown on the edit dialog so the effect of the setting is visible.
  expiresAt: { type: String, default: null }
})
const emit = defineEmits(['update:modelValue'])

const UNITS = [{ key: 'hours', label: 'hours' }, { key: 'days', label: 'days' }]
const BASES = [
  { key: 'lastActivity', label: 'after last activity', hint: 'attaching, typing or messaging restarts the countdown' },
  { key: 'start', label: 'after start', hint: 'counted from when the session was created' }
]

function patch(changes) { emit('update:modelValue', { ...props.modelValue, ...changes }) }
const remaining = computed(() => formatRemaining(props.expiresAt))
const soon = computed(() => isExpiringSoon(props.expiresAt))
const baseHint = computed(() => BASES.find(b => b.key === props.modelValue.from)?.hint)
</script>
<template>
  <div class="card sect" data-auto-delete>
    <div class="head">
      <label>Auto-delete <span class="dim">— remove the session on its own after a while</span></label>
      <button type="button" class="toggle" role="switch" data-auto-delete-toggle
              :aria-checked="modelValue.enabled ? 'true' : 'false'" :class="{ on: modelValue.enabled }"
              @click="patch({ enabled: !modelValue.enabled })">
        <span class="knob"></span>
        <span class="toggle-label">{{ modelValue.enabled ? 'On' : 'Off' }}</span>
      </button>
    </div>
    <template v-if="modelValue.enabled">
      <div class="row">
        <input type="number" min="1" step="any" class="amount" data-auto-delete-amount :value="modelValue.amount"
               aria-label="Auto-delete after" @input="patch({ amount: $event.target.value })" />
        <div class="chips-box">
          <button v-for="u in UNITS" :key="u.key" type="button" class="chip" :class="{ on: modelValue.unit === u.key }"
                  :aria-pressed="modelValue.unit === u.key" :data-auto-delete-unit="u.key"
                  @click="patch({ unit: u.key })">{{ u.label }}</button>
        </div>
        <div class="chips-box">
          <button v-for="b in BASES" :key="b.key" type="button" class="chip" :class="{ on: modelValue.from === b.key }"
                  :aria-pressed="modelValue.from === b.key" :data-auto-delete-from="b.key"
                  :disabled="scheduled && b.key !== 'start'"
                  @click="patch({ from: b.key })">{{ b.label }}</button>
        </div>
      </div>
      <small class="hint">
        {{ scheduled ? 'A scheduled session runs from a CronJob nobody attaches to, so it can only count from its start.' : baseHint }}
        Minimum 5 minutes, maximum 365 days. S3 artifacts are kept.
      </small>
      <p v-if="expiresAt" class="expires" :class="{ soon }" data-auto-delete-expires>
        {{ remaining === 'expired' ? 'Due for deletion on the next sweep.' : `Expires in ${remaining}.` }}
      </p>
    </template>
  </div>
</template>
<style scoped>
.sect { padding: 18px 20px; margin-bottom: 16px; }
.head { display: flex; align-items: center; justify-content: space-between; gap: 12px; flex-wrap: wrap; }
.head label { margin: 0; }
.dim { color: var(--faint); font-weight: 400; }
.row { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; margin-top: 12px; }
.amount { width: 90px; }
.chips-box { display: inline-flex; gap: 2px; background: var(--input); border: 1px solid var(--border-2); border-radius: var(--radius); padding: 3px; }
.chip { font-size: 12px; font-weight: 700; padding: 6px 12px; border-radius: 8px; border: none; background: none; color: var(--muted-3); }
.chip:hover { color: var(--text); background: none; }
.chip.on { background: var(--border-2); color: var(--strong); }
.chip:disabled { opacity: .4; cursor: not-allowed; }
.hint { display: block; margin-top: 8px; color: var(--muted-3); font-size: 12px; line-height: 1.5; }
.expires { margin: 8px 0 0; font-size: 12px; font-weight: 700; color: var(--accent-2); }
.expires.soon { color: var(--warn); }
.toggle { display: inline-flex; align-items: center; gap: 10px; width: auto; padding: 5px 14px 5px 6px; border: 1px solid var(--border-2); border-radius: 999px; background: none; font-size: 12px; color: var(--muted-3); }
.toggle:hover { background: var(--hover); }
.toggle .knob { width: 30px; height: 17px; padding: 2px; border-radius: 999px; background: var(--border-3); transition: background .15s; }
.toggle .knob::after { content: ''; display: block; width: 13px; height: 13px; border-radius: 50%; background: var(--panel); transition: transform .15s; }
.toggle.on { border-color: var(--accent); color: var(--accent); }
.toggle.on .knob { background: var(--accent); }
.toggle.on .knob::after { transform: translateX(13px); }
.toggle-label { font-weight: 700; }
</style>
