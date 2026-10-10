<script setup>
import { inject, ref } from 'vue'
import { LICENSE_SETTINGS_PATH, LICENSE_SETTINGS_TAB } from '../lib/license.js'

// The one card every enterprise feature shows when the backend answers 402. Each pane used to
// draw its own ("Enterprise license required for sharing.", "Library sharing is an enterprise
// feature", a raw `402 {"error":…}` in the share dialog) — the texts drifted and none of them
// said where to go. docs/frontend-license-gating.md explains the choice.
const props = defineProps({
  // What the viewer was trying to do, as a noun phrase: "Sharing sessions with other users".
  feature: { type: String, required: true }
})

// App.vue provides both; a pane mounted on its own (component tests, the standalone admin page)
// falls back to "not an admin" and a plain link, which is the safe reading of "unknown".
const isAdmin = inject('isAdmin', ref(false))
const openSettings = inject('openSettings', null)

const path = LICENSE_SETTINGS_PATH

function activate(event) {
  if (!openSettings) return // no provider: the anchor's href navigates the old way
  event.preventDefault()
  openSettings(LICENSE_SETTINGS_TAB)
}
</script>

<template>
  <div class="gate" data-license-gate role="status">
    <svg class="lock" viewBox="0 0 20 20" aria-hidden="true" focusable="false">
      <rect x="4" y="8.5" width="12" height="9" rx="2" fill="currentColor" opacity=".18" />
      <rect x="4" y="8.5" width="12" height="9" rx="2" fill="none" stroke="currentColor" stroke-width="1.5" />
      <path d="M7 8.5V6.5a3 3 0 0 1 6 0v2" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" />
      <circle cx="10" cy="13" r="1.2" fill="currentColor" />
    </svg>
    <div class="body">
      <div class="title">Enterprise feature</div>
      <p class="text">{{ feature }} needs an active enterprise license.</p>
      <a v-if="isAdmin" class="link" :href="path" data-license-link @click="activate">Activate a license →</a>
      <p v-else class="hint" data-license-hint>Ask an administrator to activate a license.</p>
    </div>
  </div>
</template>

<style scoped>
.gate { display: flex; gap: 12px; align-items: flex-start; padding: 14px 16px; border: 1px dashed var(--border-3); border-radius: var(--radius); background: var(--panel); color: var(--muted); }
.lock { width: 22px; height: 22px; flex-shrink: 0; color: var(--warn); margin-top: 1px; }
.body { min-width: 0; }
.title { font-weight: 700; font-size: 13px; color: var(--strong); }
.text, .hint { margin: 3px 0 0; font-size: 12px; line-height: 1.5; color: var(--muted); }
.link { display: inline-block; margin-top: 6px; font-size: 12px; font-weight: 600; color: var(--accent); }
.link:hover { color: var(--accent-2); }
</style>
