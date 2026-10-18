<script setup>
// Which stored git personal access tokens a session is built with (docs/credential-scopes.md).
// One choice per host, because a session uses at most one token per host: a host with a single
// token is "with it or without it" — a real choice for a session that must not carry the company
// token — and a host with several picks one of them by label, or none.
import { computed } from 'vue'
import { gitPatHosts, gitPatName } from '../lib/agent.js'

const props = defineProps({
  // {id, kind, host, label, isDefault} as the credential status lists them — never a token.
  options: { type: Array, default: () => [] },
  // Ids currently chosen, at most one per host. Every host on its default means "the defaults,
  // including tokens stored later".
  modelValue: { type: Array, default: () => [] }
})
const emit = defineEmits(['update:modelValue'])
const kindLabel = kind => (kind === 'github' ? 'GitHub' : 'GitLab')
const hosts = computed(() => gitPatHosts(props.options))

function chosenFor(group) {
  return group.tokens.find(pat => props.modelValue.includes(pat.id))?.id || ''
}

function choose(group, id) {
  const others = props.modelValue.filter(item => !group.tokens.some(pat => pat.id === item))
  emit('update:modelValue', id ? [...others, id] : others)
}
</script>

<template>
  <div v-if="options.length" class="field pats" data-git-pat-picker>
    <label>Git tokens <span class="dim">— stored personal access tokens this session may use, one per host</span></label>
    <template v-for="group in hosts" :key="group.host">
      <label v-if="group.tokens.length === 1" class="check">
        <input type="checkbox" :checked="!!chosenFor(group)" :data-git-pat-option="group.tokens[0].id"
          @change="choose(group, $event.target.checked ? group.tokens[0].id : '')" />
        <span>{{ group.host }} <span class="kind">{{ kindLabel(group.kind) }}</span>
          <span v-if="group.tokens[0].label" class="dim">{{ group.tokens[0].label }}</span></span>
      </label>
      <div v-else class="host">
        <span>{{ group.host }} <span class="kind">{{ kindLabel(group.kind) }}</span></span>
        <select :value="chosenFor(group)" :data-git-pat-host="group.host" @change="choose(group, $event.target.value)">
          <option v-for="pat in group.tokens" :key="pat.id" :value="pat.id" :data-git-pat-choice="pat.id">
            {{ gitPatName(pat) }}{{ pat.isDefault ? ' (default)' : '' }}
          </option>
          <option value="">No token for this host</option>
        </select>
      </div>
    </template>
    <small class="hint" data-git-pat-hint>
      Applies on the next start or resume. Left on the defaults, the session also gets tokens for hosts stored later.
    </small>
  </div>
</template>

<style scoped>
.pats { margin-top: 14px; }
.dim { color: var(--faint); font-weight: 400; }
.check { display: flex; align-items: center; gap: 10px; margin: 4px 0 0; font-size: 13px; color: var(--text); cursor: pointer; }
.check input { width: auto; }
.host { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; margin: 6px 0 0; font-size: 13px; color: var(--text); }
.host select { width: auto; min-width: 0; max-width: 100%; }
.kind { display: inline-block; margin-left: 6px; padding: 1px 6px; border: 1px solid var(--border-2); border-radius: 4px; color: var(--muted-3); font-size: 11px; font-weight: 700; }
.hint { display: block; margin-top: 6px; color: var(--muted-3); font-size: 12px; }
</style>
