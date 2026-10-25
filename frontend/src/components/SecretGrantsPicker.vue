<script setup>
import { computed } from 'vue'
import { hasNamesake } from '../lib/secrets.js'

const props = defineProps({
  options: { type: Array, default: () => [] },
  modelValue: { type: Array, default: () => [] },
  // The session's effective secrets, so a secret that only the project selects is labelled as
  // such: without it, "Not allowed" on that row reads as if the agent had no access at all.
  effective: { type: Array, default: () => [] },
  disabled: { type: Boolean, default: false }
})
const emit = defineEmits(['update:modelValue'])

// One choice per secret instead of a grant checkbox plus an inheritance checkbox: the second
// checkbox only appeared once the first was ticked, so the row jumped under the pointer, and the
// pair offered no single place to see what a child session would get.
const levels = [
  { value: 'none', label: 'Not allowed' },
  { value: 'session', label: 'This session' },
  { value: 'children', label: 'Also child sessions' }
]

const grantOf = id => props.modelValue.find(g => g.secretId === id)
const levelOf = id => {
  const grant = grantOf(id)
  return !grant ? 'none' : grant.inheritToChildren ? 'children' : 'session'
}
const provenanceOf = id => grantOf(id) ? grantOf(id).source || 'granted' : props.effective.find(s => s.id === id)?.provenance
const hasInherited = computed(() => props.modelValue.some(g => g.source === 'inherited'))

function choose(id, level) {
  if (props.disabled || level === levelOf(id)) return
  // A new grant inherits only when the owner picks "Also child sessions" (#209).
  emit('update:modelValue', level === 'none'
    ? props.modelValue.filter(g => g.secretId !== id)
    : grantOf(id)
      ? props.modelValue.map(g => g.secretId === id ? { ...g, inheritToChildren: level === 'children' } : g)
      : [...props.modelValue, { secretId: id, inheritToChildren: level === 'children' }])
}
function makeDirect(id) {
  emit('update:modelValue', props.modelValue.map(g => g.secretId === id ? { ...g, source: 'granted' } : g))
}
// Radio-group keyboard behaviour: arrows move and select, Home/End jump, and only the checked
// option is in the tab order, so tabbing through a long list costs one stop per secret.
function onKey(event, id) {
  const index = levels.findIndex(l => l.value === levelOf(id))
  const step = { ArrowRight: 1, ArrowDown: 1, ArrowLeft: -1, ArrowUp: -1 }[event.key]
  let next
  if (step) next = (index + step + levels.length) % levels.length
  else if (event.key === 'Home') next = 0
  else if (event.key === 'End') next = levels.length - 1
  else return
  event.preventDefault()
  choose(id, levels[next].value)
  event.currentTarget.parentElement.children[next]?.focus()
}
</script>
<template>
  <fieldset v-if="options.length" class="grants" data-secret-grants>
    <legend>Session grants</legend>
    <p>Grant a stored secret to this session beyond the project selection. Child sessions receive it only with “Also child sessions”.</p>
    <div v-for="secret in options" :key="secret.id" class="row" :data-secret-row="secret.id">
      <span :id="`grant-${secret.id}`" class="name">
        {{ secret.name }}
        <small v-if="hasNamesake(options, secret)" class="id" :data-secret-id="secret.id">{{ secret.id }}</small>
        <small v-if="provenanceOf(secret.id)" class="provenance" :data-secret-provenance="secret.id">{{ provenanceOf(secret.id) }}</small>
      </span>
      <div class="segmented" role="radiogroup" :aria-labelledby="`grant-${secret.id}`" :aria-disabled="disabled || null" :data-secret-grant="secret.id">
        <button v-for="level in levels" :key="level.value" type="button" role="radio"
          :class="{ on: levelOf(secret.id) === level.value }"
          :aria-checked="levelOf(secret.id) === level.value"
          :tabindex="levelOf(secret.id) === level.value ? 0 : -1"
          :disabled="disabled"
          :data-grant-level="level.value"
          @click="choose(secret.id, level.value)" @keydown="onKey($event, secret.id)">{{ level.label }}</button>
      </div>
      <button v-if="!disabled && grantOf(secret.id)?.source === 'inherited'" type="button" class="direct" @click="makeDirect(secret.id)">Make direct grant</button>
    </div>
    <small v-if="hasInherited">Saving a changed grant list approves its inherited entries as direct owner grants, independent of the parent.</small>
    <small>“Not allowed” revokes the grant; a secret the project selects stays available. Files update live; variables change at the next start. Pause the session to revoke a variable immediately.</small>
  </fieldset>
</template>
<style scoped>
.grants { margin-top: 14px; border: 1px solid var(--border-2); border-radius: 6px; }
p, small { display: block; color: var(--muted-3); font-size: 12px; }
.row { display: flex; flex-wrap: wrap; align-items: center; gap: 6px 10px; margin: 8px 0; font-size: 13px; }
.name { flex: 1 1 140px; min-width: 0; overflow-wrap: anywhere; }
.provenance { display: inline; margin-left: 6px; }
.id { display: inline; margin-left: 6px; font-family: var(--mono); font-size: 11px; color: var(--faint); }
/* The look of the session bar's Workspace | Terminal | Shell switch (TerminalView .tabs). */
.segmented { display: flex; flex-wrap: wrap; gap: 2px; max-width: 100%; background: var(--panel); border: 1px solid var(--border-2); border-radius: var(--radius); padding: 3px; }
.segmented button { width: auto; font-size: 12px; font-weight: 700; padding: 5px 12px; border-radius: 8px; border: none; background: none; color: var(--muted-3); white-space: nowrap; }
.segmented button:hover:not(:disabled) { color: var(--text); background: none; }
.segmented button.on { background: var(--border-2); color: var(--strong); }
.segmented button:disabled { cursor: default; }
.direct { width: auto; font-size: 12px; }
</style>
