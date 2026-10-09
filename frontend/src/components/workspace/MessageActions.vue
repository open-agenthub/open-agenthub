<script setup>
import { onBeforeUnmount, ref } from 'vue'
const props = defineProps({ text: String, canQuote: Boolean })
defineEmits(['quote'])
const copied = ref(false)
const error = ref('')
let timer
async function copy() {
  error.value = ''
  try {
    if (!navigator.clipboard?.writeText) throw new Error('Clipboard unavailable')
    await navigator.clipboard.writeText(props.text || '')
    copied.value = true
    clearTimeout(timer)
    timer = setTimeout(() => { copied.value = false }, 2000)
  } catch { error.value = 'Could not copy. Select the message text to copy it.' }
}
onBeforeUnmount(() => clearTimeout(timer))
</script>
<template>
  <div class="message-actions">
    <button type="button" aria-label="Copy message" :disabled="copied" @click="copy">{{ copied ? 'Copied' : 'Copy' }}</button>
    <button v-if="canQuote" type="button" aria-label="Quote message" @click="$emit('quote', text)">Quote</button>
    <span v-if="error" role="status">{{ error }}</span>
  </div>
</template>
<style scoped>
.message-actions { display: flex; align-items: center; gap: 6px; margin-top: 8px; flex-wrap: wrap; }
button { color: var(--muted-3); background: transparent; border: 0; padding: 3px 6px; border-radius: 5px; font-size: 11px; }
button:hover { color: var(--text); background: var(--hover); }
span { color: var(--danger); font-size: 11px; }
</style>
