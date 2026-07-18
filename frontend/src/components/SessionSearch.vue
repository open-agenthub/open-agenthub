<script setup>
import { computed, ref, watch } from 'vue'
import { sessionMatches, repoShortName } from '../lib/text.js'
import { sessionStatus, statusStyle } from '../lib/status.js'

const props = defineProps({
  sessions: { type: Array, default: () => [] },
  modelValue: { type: String, default: '' }
})
const emit = defineEmits(['update:modelValue', 'select'])

const input = ref(null)
const open = ref(false)
const highlight = ref(0)

const results = computed(() => props.modelValue.trim()
  ? props.sessions.filter(s => sessionMatches(s, props.modelValue)).slice(0, 8)
  : [])

watch(results, r => { if (highlight.value >= r.length) highlight.value = Math.max(0, r.length - 1) })

function onInput(e) {
  emit('update:modelValue', e.target.value)
  open.value = true
  highlight.value = 0
}
function choose(s) {
  emit('select', s.id)
  emit('update:modelValue', '')
  open.value = false
  input.value?.blur()
}
function onKeydown(e) {
  if (!open.value || !results.value.length) {
    if (e.key === 'Escape') input.value?.blur()
    return
  }
  if (e.key === 'ArrowDown') { e.preventDefault(); highlight.value = (highlight.value + 1) % results.value.length }
  else if (e.key === 'ArrowUp') { e.preventDefault(); highlight.value = (highlight.value - 1 + results.value.length) % results.value.length }
  else if (e.key === 'Enter') { e.preventDefault(); choose(results.value[highlight.value]) }
  else if (e.key === 'Escape') { open.value = false; input.value?.blur() }
}
// Delay closing so a mousedown on a result still lands.
function onBlur() { setTimeout(() => { open.value = false }, 120) }

defineExpose({ focus: () => input.value?.focus() })

function repoLabel(s) { return repoShortName(s.repoUrl || s.repos?.[0]?.url || '') }
</script>
<template>
  <div class="search-wrap">
    <div class="search">
      <span class="search-icon">⌕</span>
      <input ref="input" :value="modelValue" placeholder="Find a session…" role="combobox"
        :aria-expanded="open && results.length > 0" aria-autocomplete="list"
        @input="onInput" @keydown="onKeydown" @focus="open = true" @blur="onBlur" />
      <span class="kbd">⌘K</span>
    </div>
    <ul v-if="open && results.length" class="results" role="listbox">
      <li v-for="(s, i) in results" :key="s.id" role="option" :aria-selected="i === highlight"
        :class="{ hl: i === highlight }" :data-search-result="s.id"
        @mousedown.prevent="choose(s)" @mousemove="highlight = i">
        <span class="dot" :style="{ background: statusStyle(s).color }"></span>
        <span class="r-title">{{ s.title }}</span>
        <span class="r-meta"><span :style="{ color: statusStyle(s).color }">{{ sessionStatus(s) }}</span><template v-if="repoLabel(s)"> · {{ repoLabel(s) }}</template></span>
      </li>
    </ul>
    <div v-else-if="open && modelValue.trim()" class="results empty">No matching sessions.</div>
  </div>
</template>
<style scoped>
.search-wrap { position: relative; flex: 1; max-width: 400px; }
.search { display: flex; align-items: center; gap: 8px; background: var(--hover); border: 1px solid var(--border-2); border-radius: var(--radius); padding: 0 12px; color: var(--muted-3); }
.search input { flex: 1; background: none; border: none; padding: 8px 0; font-size: 14px; }
.search input:focus { outline: none; }
.search:focus-within { border-color: var(--accent); }
.kbd { font-family: var(--mono); font-size: 11px; border: 1px solid var(--border-3); border-radius: 5px; padding: 0 5px; }
.results { position: absolute; top: 100%; left: 0; right: 0; margin: 6px 0 0; padding: 5px; list-style: none; background: var(--panel); border: 1px solid var(--border-3); border-radius: var(--radius); box-shadow: 0 12px 36px rgba(0,0,0,0.5); z-index: 60; max-height: 320px; overflow-y: auto; }
.results.empty { padding: 12px 14px; color: var(--muted-3); font-size: 13px; }
.results li { display: flex; align-items: center; gap: 9px; padding: 8px 10px; border-radius: 8px; cursor: pointer; }
.results li.hl { background: var(--panel-2); }
.dot { width: 7px; height: 7px; border-radius: 50%; flex-shrink: 0; }
.r-title { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-size: 13px; color: var(--text); }
.r-meta { margin-left: auto; flex-shrink: 0; font-size: 11px; color: var(--muted-3); font-family: var(--mono); }
</style>
