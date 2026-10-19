<script setup>
const props = defineProps({ options: { type: Array, default: () => [] }, modelValue: { type: Array, default: () => [] } })
const emit = defineEmits(['update:modelValue'])
function toggle(id) {
  emit('update:modelValue', props.modelValue.some(g => g.secretId === id)
    ? props.modelValue.filter(g => g.secretId !== id)
    : [...props.modelValue, { secretId: id, inheritToChildren: false }])
}
function makeDirect(id) {
  emit('update:modelValue', props.modelValue.map(g => g.secretId === id ? { ...g, source: 'granted' } : g))
}
function inherit(id, value) {
  emit('update:modelValue', props.modelValue.map(g => g.secretId === id ? { ...g, inheritToChildren: value } : g))
}
</script>
<template>
  <fieldset v-if="options.length" class="grants" data-secret-grants>
    <legend>Session grants</legend>
    <p>Grant a stored secret to this session beyond the project selection. Child sessions receive it only when inheritance is enabled.</p>
    <div v-for="secret in options" :key="secret.id">
      <label><input type="checkbox" :data-secret-grant="secret.id" :checked="modelValue.some(g => g.secretId === secret.id)" @change="toggle(secret.id)"> {{ secret.name }} <small v-if="modelValue.some(g => g.secretId === secret.id)">{{ modelValue.find(g => g.secretId === secret.id)?.source || 'granted' }}</small></label>
      <button v-if="modelValue.find(g => g.secretId === secret.id)?.source === 'inherited'" type="button" @click="makeDirect(secret.id)">Make direct grant</button>
      <label v-if="modelValue.some(g => g.secretId === secret.id)"><input type="checkbox" :data-secret-inherit="secret.id" :checked="modelValue.find(g => g.secretId === secret.id)?.inheritToChildren" @change="inherit(secret.id, $event.target.checked)"> Inherit to children</label>
    </div>
    <small v-if="modelValue.some(g => g.source === 'inherited')">Saving a changed grant list approves its inherited entries as direct owner grants, independent of the parent.</small>
    <small>Untick to revoke. Files update live; variables change at the next start. Pause the session to revoke a variable immediately.</small>
  </fieldset>
</template>
<style scoped>
.grants { margin-top: 14px; border: 1px solid var(--border-2); border-radius: 6px; }
p, small { color: var(--muted-3); font-size: 12px; }
label { display: flex; align-items: center; gap: 8px; margin: 6px 0; font-size: 13px; }
input { width: auto; }
</style>
