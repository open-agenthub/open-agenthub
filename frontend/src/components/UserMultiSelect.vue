<script setup>
import { computed, ref } from 'vue'

// Dependency-free multi-select for usernames: selected users render as
// removable chips, the text input filters the known-users list and matching,
// not-yet-selected users appear as clickable suggestions.
const props = defineProps({
  modelValue: { type: Array, default: () => [] },   // owner strings
  users: { type: Array, default: () => [] },        // [{owner, displayName, email}]
  disabled: { type: Boolean, default: false }
})
const emit = defineEmits(['update:modelValue'])

const query = ref('')

const byOwner = computed(() => new Map(props.users.map(u => [u.owner, u])))
function label(owner) {
  const u = byOwner.value.get(owner)
  return u?.displayName || owner
}

const suggestions = computed(() => {
  const q = query.value.trim().toLowerCase()
  if (!q) return []
  return props.users.filter(u =>
    !props.modelValue.includes(u.owner) &&
    [u.owner, u.displayName, u.email].some(v => v && v.toLowerCase().includes(q))
  )
})

function add(owner) {
  if (props.disabled || props.modelValue.includes(owner)) return
  emit('update:modelValue', [...props.modelValue, owner])
  query.value = ''
}

function remove(owner) {
  if (props.disabled) return
  emit('update:modelValue', props.modelValue.filter(o => o !== owner))
}

function onEnter() {
  const q = query.value.trim().toLowerCase()
  if (!q) return
  const exact = suggestions.value.find(u =>
    [u.owner, u.displayName, u.email].some(v => v && v.toLowerCase() === q))
  if (exact) add(exact.owner)
}
</script>

<template>
  <div class="ums" :class="{ disabled }">
    <div class="box">
      <span v-for="owner in modelValue" :key="owner" class="chip" data-user-chip>
        <span class="chip-label">{{ label(owner) }}</span>
        <button type="button" class="chip-x" :disabled="disabled" :aria-label="`Remove ${label(owner)}`" @click="remove(owner)">✕</button>
      </span>
      <input v-model="query" class="q" :disabled="disabled" placeholder="Type to add users…"
        @keydown.enter.prevent="onEnter" />
    </div>
    <div v-if="suggestions.length" class="sugg">
      <button v-for="u in suggestions" :key="u.owner" type="button" class="sugg-row" data-user-suggestion
        :disabled="disabled" @click="add(u.owner)">
        <span class="sugg-name">{{ u.displayName || u.owner }}</span>
        <span class="sugg-meta">{{ u.email || u.owner }}</span>
      </button>
    </div>
  </div>
</template>

<style scoped>
.ums { position: relative; }
.ums.disabled { opacity: .6; }
.box {
  display: flex; flex-wrap: wrap; align-items: center; gap: 6px;
  background: var(--input); border: 1px solid var(--border-2); border-radius: 10px; padding: 6px 8px;
}
.box:focus-within { border-color: var(--accent); }
.chip {
  display: inline-flex; align-items: center; gap: 5px;
  background: var(--panel-2); border: 1px solid var(--border-3); border-radius: 999px;
  padding: 2px 4px 2px 10px; font-size: 12px; color: var(--text);
}
.chip-label { max-width: 180px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.chip-x { border: none; background: none; color: var(--muted); font-size: 10px; padding: 2px 6px; border-radius: 999px; }
.chip-x:hover { color: var(--danger); background: none; }
.q {
  flex: 1; min-width: 120px; width: auto; border: none; background: none;
  padding: 3px 4px; font-size: 13px; color: var(--text);
}
.q:focus { outline: none; border: none; }
.sugg {
  margin-top: 4px; border: 1px solid var(--border-2); border-radius: 10px;
  background: var(--panel); overflow: hidden; max-height: 180px; overflow-y: auto;
}
.sugg-row {
  display: flex; align-items: baseline; gap: 10px; width: 100%; text-align: left;
  border: none; border-radius: 0; background: none; padding: 7px 12px; font-weight: 400;
}
.sugg-row:hover { background: var(--hover); }
.sugg-name { font-size: 13px; color: var(--text); }
.sugg-meta { font-size: 11px; color: var(--muted-3); font-family: var(--mono); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
</style>
