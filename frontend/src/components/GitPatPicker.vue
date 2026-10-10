<script setup>
// Which stored git personal access tokens a session is built with (docs/credential-scopes.md).
// Rendered from the first stored token: with one token the choice is "with it or without it",
// which is a real choice for a session that must not carry the company token.
const props = defineProps({
  // {id, kind, host} as the credential status lists them — never a token.
  options: { type: Array, default: () => [] },
  // Ids currently ticked. Every option ticked means "all, including ones added later".
  modelValue: { type: Array, default: () => [] }
})
const emit = defineEmits(['update:modelValue'])
const kindLabel = kind => (kind === 'github' ? 'GitHub' : 'GitLab')

function toggle(id) {
  const next = props.modelValue.includes(id)
    ? props.modelValue.filter(item => item !== id)
    : [...props.modelValue, id]
  emit('update:modelValue', next)
}
</script>

<template>
  <div v-if="options.length" class="field pats" data-git-pat-picker>
    <label>Git tokens <span class="dim">— stored personal access tokens this session may use</span></label>
    <label v-for="pat in options" :key="pat.id" class="check">
      <input type="checkbox" :checked="modelValue.includes(pat.id)" :data-git-pat-option="pat.id" @change="toggle(pat.id)" />
      <span>{{ pat.host }} <span class="kind">{{ kindLabel(pat.kind) }}</span></span>
    </label>
    <small class="hint" data-git-pat-hint>
      Applies on the next start or resume. With every token ticked the session also gets tokens stored later;
      untick one to keep it out of this session.
    </small>
  </div>
</template>

<style scoped>
.pats { margin-top: 14px; }
.dim { color: var(--faint); font-weight: 400; }
.check { display: flex; align-items: center; gap: 10px; margin: 4px 0 0; font-size: 13px; color: var(--text); cursor: pointer; }
.check input { width: auto; }
.kind { display: inline-block; margin-left: 6px; padding: 1px 6px; border: 1px solid var(--border-2); border-radius: 4px; color: var(--muted-3); font-size: 11px; font-weight: 700; }
.hint { display: block; margin-top: 6px; color: var(--muted-3); font-size: 12px; }
</style>
