<script setup>
// The "⋯" menu of the session bar (#551): everything a person reaches for now and then, so the bar
// itself keeps only what is used all the time. A disclosure (button + panel) rather than an ARIA
// menu, because the panel holds a <select> and a role="menu" may only contain menu items.
import { nextTick, onBeforeUnmount, ref, watch } from 'vue'

const props = defineProps({
  // Shown as a dot on the button: something in the menu wants attention (open messages, an
  // outdated image) and would otherwise be invisible behind a closed menu.
  attention: { type: String, default: '' }
})
const open = ref(false)
const root = ref(null)
const button = ref(null)
const panel = ref(null)
const panelId = `session-menu-${Math.random().toString(36).slice(2, 10)}`

const focusable = () => [...(panel.value?.querySelectorAll('button:not([disabled]), select:not([disabled])') || [])]

async function toggle() {
  open.value = !open.value
  if (open.value) { await nextTick(); focusable()[0]?.focus() }
}
function close({ refocus = false } = {}) {
  if (!open.value) return
  open.value = false
  if (refocus) button.value?.focus()
}
function onKeydown(event) {
  if (event.key === 'Escape') { event.stopPropagation(); close({ refocus: true }); return }
  if (event.key !== 'ArrowDown' && event.key !== 'ArrowUp') return
  // A select keeps its arrow keys: they change its value.
  if (event.target?.tagName === 'SELECT') return
  const items = focusable()
  if (!items.length) return
  event.preventDefault()
  const at = items.indexOf(document.activeElement)
  const next = event.key === 'ArrowDown' ? (at + 1) % items.length : (at - 1 + items.length) % items.length
  items[next].focus()
}
// A click outside or focus leaving the menu closes it; an open panel left behind would cover the
// conversation after the person has moved on.
function onPointerDown(event) { if (!root.value?.contains(event.target)) close() }
function onFocusOut(event) { if (event.relatedTarget && !root.value?.contains(event.relatedTarget)) close() }
watch(open, value => {
  if (value) document.addEventListener('pointerdown', onPointerDown, true)
  else document.removeEventListener('pointerdown', onPointerDown, true)
})
onBeforeUnmount(() => document.removeEventListener('pointerdown', onPointerDown, true))
defineExpose({ close })
</script>

<template>
  <div ref="root" class="session-menu" data-session-menu @keydown="onKeydown" @focusout="onFocusOut">
    <button ref="button" type="button" class="menu-btn" data-session-menu-toggle :aria-expanded="open ? 'true' : 'false'"
      :aria-controls="panelId" :aria-label="attention ? `Session menu — ${attention}` : 'Session menu'"
      :title="attention || 'More session actions'" @click="toggle">
      <span aria-hidden="true">⋯</span><span v-if="attention" class="attention" data-session-menu-attention aria-hidden="true"></span>
    </button>
    <!-- v-show, not v-if: the items keep their state and the components inside (the account
         picker, the outdated-image action) stay mounted while the menu is closed. -->
    <div v-show="open" :id="panelId" ref="panel" class="panel" role="group" aria-label="Session actions" data-session-menu-panel
      @click="$event.target.closest('[data-menu-closes]') && close()">
      <slot />
    </div>
  </div>
</template>

<style scoped>
.session-menu { position: relative; display: inline-flex; flex-shrink: 0; }
.menu-btn { position: relative; width: 32px; height: 30px; padding: 0; border-radius: 9px; font-size: 18px; line-height: 1; display: inline-flex; align-items: center; justify-content: center; }
.menu-btn[aria-expanded='true'] { background: var(--border-2); color: var(--strong); }
.attention { position: absolute; top: 4px; right: 4px; width: 7px; height: 7px; border-radius: 50%; background: var(--accent); box-shadow: 0 0 0 2px var(--bg); }
.panel { position: absolute; top: calc(100% + 8px); right: 0; z-index: 60; width: max-content; min-width: 200px; max-width: min(320px, calc(100vw - 32px)); max-height: 70vh; overflow-y: auto;
  display: flex; flex-direction: column; gap: 2px; padding: 6px; background: var(--panel); border: 1px solid var(--border-3); border-radius: var(--radius-lg); box-shadow: 0 16px 48px rgba(0,0,0,0.5); }
</style>
