<script setup>
// What a session gets of each stored secret (docs/session-secrets.md, #329): one three-way choice
// per secret that edits both the selection and the grants. Until #329 the session dialogs showed a
// checkbox list for the selection above these buttons for the grants, two controls for one
// question, and a secret could be ticked in one and "Not allowed" in the other.
import { computed } from 'vue'
import { GRANT_LEVELS, deliveryLabel, hasNamesake, secretLevel, withSecretLevel } from '../lib/secrets.js'
import SecretLevels from './SecretLevels.vue'

const props = defineProps({
  // Every stored secret: a grant may go beyond the project's selection.
  options: { type: Array, default: () => [] },
  // The grants.
  modelValue: { type: Array, default: () => [] },
  // The ticked selection (secretSelectionFor) and what it can hold (secretOptions).
  selected: { type: Array, default: () => [] },
  selectable: { type: Array, default: () => [] },
  inProject: { type: Boolean, default: false },
  // The session's effective secrets, so a secret it holds some other way (inherited from its
  // parent, say) is labelled as such rather than looking unused.
  effective: { type: Array, default: () => [] },
  disabled: { type: Boolean, default: false }
})
// `change` carries both halves of one choice, for a caller that saves them in one request.
const emit = defineEmits(['update:modelValue', 'update:selected', 'change'])

const grantOf = id => props.modelValue.find(g => g.secretId === id)
const levelOf = id => secretLevel(id, props.selected, props.modelValue)
const provenanceOf = id => grantOf(id) ? grantOf(id).source || 'granted'
  : props.selected.includes(id) ? (props.inProject ? 'project' : 'selected')
  : props.effective.find(s => s.id === id)?.provenance
const hasInherited = computed(() => props.modelValue.some(g => g.source === 'inherited'))
// A session follows its project only while every secret the project selects is in its selection;
// lowering one makes the selection an explicit list, and a secret the project adds later then no
// longer reaches it. Said where it happens rather than discovered when an agent misses a variable.
const narrowed = computed(() => props.inProject && props.selectable.some(id => !props.selected.includes(id)))

function choose(id, level) {
  const next = withSecretLevel(id, level, { selected: props.selected, grants: props.modelValue, selectable: props.selectable })
  if (next.selected !== props.selected) emit('update:selected', next.selected)
  if (next.grants !== props.modelValue) emit('update:modelValue', next.grants)
  emit('change', next)
}
function makeDirect(id) {
  const grants = props.modelValue.map(g => g.secretId === id ? { ...g, source: 'granted' } : g)
  emit('update:modelValue', grants)
  emit('change', { selected: props.selected, grants })
}
</script>
<template>
  <fieldset v-if="options.length" class="grants" data-secret-grants>
    <legend>Secrets <span class="dim">— delivered as environment variables or files in ~/.secrets</span></legend>
    <div v-for="secret in options" :key="secret.id" class="row" :data-secret-row="secret.id">
      <span :id="`grant-${secret.id}`" class="name">
        <code>{{ secret.name }}</code>
        <small v-if="hasNamesake(options, secret)" class="id" :data-secret-id="secret.id">{{ secret.id }}</small>
        <span class="kind">{{ deliveryLabel(secret.delivery) }}</span>
        <small v-if="provenanceOf(secret.id)" class="provenance" :data-secret-provenance="secret.id">{{ provenanceOf(secret.id) }}</small>
      </span>
      <SecretLevels :levels="GRANT_LEVELS" :model-value="levelOf(secret.id)" :labelledby="`grant-${secret.id}`"
        :disabled="disabled" :data-secret-grant="secret.id" @update:model-value="choose(secret.id, $event)" />
      <button v-if="!disabled && grantOf(secret.id)?.source === 'inherited'" type="button" class="direct" @click="makeDirect(secret.id)">Make direct grant</button>
    </div>
    <small v-if="narrowed" class="follow-note" data-secret-follow-note>This session no longer follows the project: secrets the project selects later do not reach it. Set every project secret back to “Also child sessions” to follow it again.</small>
    <small v-if="hasInherited">Saving a changed grant list approves its inherited entries as direct owner grants, independent of the parent.</small>
    <small data-secret-hint>
      <template v-if="inProject">The project's secrets start at “Also child sessions”; with all of them there the session follows the project, including later changes.</template>
      <template v-else>Outside a project a session gets only what is allowed here.</template>
      “This session” keeps a secret from child sessions. Files update live; variables change at the next start. Pause the session to revoke a variable immediately.
    </small>
  </fieldset>
</template>
<style scoped>
.grants { margin-top: 14px; border: 1px solid var(--border-2); border-radius: 6px; }
legend { font-size: 13px; }
.dim { color: var(--faint); font-weight: 400; }
small { display: block; color: var(--muted-3); font-size: 12px; }
.row { display: flex; flex-wrap: wrap; align-items: center; gap: 6px 10px; margin: 8px 0; font-size: 13px; }
.name { flex: 1 1 140px; min-width: 0; overflow-wrap: anywhere; }
code { font-family: var(--mono); font-size: 12px; }
.kind { display: inline-block; margin-left: 6px; padding: 1px 6px; border: 1px solid var(--border-2); border-radius: 4px; color: var(--muted-3); font-size: 11px; font-weight: 700; }
.provenance { display: inline; margin-left: 6px; }
.id { display: inline; margin-left: 6px; font-family: var(--mono); font-size: 11px; color: var(--faint); }
.direct { width: auto; font-size: 12px; }
.follow-note { color: var(--warn); }
</style>
