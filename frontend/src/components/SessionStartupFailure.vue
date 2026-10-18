<script setup>
import { onBeforeUnmount, ref } from 'vue'

const props = defineProps({ reason: { type: String, required: true } })
const copied = ref(false)
const error = ref('')
let timer
async function copy() {
  error.value = ''
  try {
    if (!navigator.clipboard?.writeText) throw new Error('Clipboard unavailable')
    await navigator.clipboard.writeText(props.reason)
    copied.value = true
    clearTimeout(timer)
    timer = setTimeout(() => { copied.value = false }, 2000)
  } catch { error.value = 'Could not copy. Select the text to copy it.' }
}
onBeforeUnmount(() => clearTimeout(timer))
</script>
<template>
  <section class="startup-failure" role="status" aria-labelledby="startup-failure-title">
    <header>
      <strong id="startup-failure-title">Session could not start</strong>
      <button type="button" class="ghost" data-session-failure-copy :disabled="copied" @click="copy">{{ copied ? 'Copied' : 'Copy' }}</button>
      <span v-if="error" class="copy-error">{{ error }}</span>
    </header>
    <!-- Diagnostics are clone output, admission messages and pod events: line breaks and
         indentation carry meaning there, so the text keeps them and scrolls instead of being
         reflowed into the narrow sidebar row where it used to wrap over a dozen lines. -->
    <pre data-session-failure tabindex="0">{{ reason }}</pre>
  </section>
</template>
<style scoped>
.startup-failure { flex: none; margin: 12px 16px 0; border: 1px solid rgba(245,122,106,0.35); border-radius: var(--radius); background: rgba(245,122,106,0.06); }
header { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; padding: 8px 8px 8px 12px; border-bottom: 1px solid rgba(245,122,106,0.2); }
strong { color: var(--danger); font-size: 13px; }
header button { margin-left: auto; font-size: 12px; }
.copy-error { flex-basis: 100%; color: var(--danger); font-size: 11px; }
/* The mono face draws != as a single ≠ glyph; someone retyping a flag from the diagnostic must see the characters that are there. */
pre { font-variant-ligatures: none; margin: 0; padding: 10px 12px; max-height: 40vh; overflow: auto; font: 12px/1.5 var(--mono); color: var(--text); white-space: pre-wrap; overflow-wrap: anywhere; user-select: text; }
</style>
