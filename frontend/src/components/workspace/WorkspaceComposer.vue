<script setup>
import { onBeforeUnmount, ref, watch } from 'vue'
import { workspaceDrafts as drafts } from '../../lib/session-drafts.js'
const props = defineProps({ sessionId: String, enabled: Boolean, status: String, agent: String, send: Function, interrupt: Function })
const text = ref(drafts.get(props.sessionId) || '')
const input = ref(null)
const pending = ref(false)
const error = ref('')
let generation = 0
watch(() => props.sessionId, (id, previous) => {
  if (previous) drafts.set(previous, text.value)
  generation += 1
  text.value = drafts.get(id) || ''
  pending.value = false
  error.value = ''
})
async function submit() {
  if (!props.enabled || pending.value || !text.value.trim()) return
  const value = text.value
  const current = generation
  pending.value = true
  error.value = ''
  try {
    await props.send(value)
    if (generation === current && text.value === value) text.value = ''
  } catch (e) { if (generation === current) error.value = e.message || 'Message could not be sent. Your draft is still here.' }
  finally { if (generation === current) pending.value = false }
}
async function stop() {
  try { await props.interrupt() } catch { error.value = 'Could not interrupt. Open the terminal to check the connection.' }
}
function keydown(event) {
  if (event.key === 'Enter' && !event.shiftKey && !event.isComposing) {
    event.preventDefault()
    void submit()
  }
}
function quote(value) {
  text.value += `${text.value ? '\n\n' : ''}${String(value).split('\n').map(line => '> ' + line).join('\n')}\n\n`
  input.value?.focus()
}
defineExpose({ quote })
onBeforeUnmount(() => { drafts.set(props.sessionId, text.value); generation += 1 })
</script>
<template>
  <form class="workspace-composer" @submit.prevent="submit" data-workspace-composer>
    <div class="composer-card">
      <textarea ref="input" v-model="text" :disabled="!enabled || pending" rows="3" aria-label="Message the agent"
        placeholder="Give your agent a task…" @keydown="keydown"></textarea>
      <div class="composer-controls">
        <span>{{ agent || 'Agent' }} <span class="connection">· {{ status }}</span></span>
        <span class="composer-shortcut">Enter to send · Shift+Enter for newline</span>
        <button type="button" :disabled="!enabled || pending" aria-label="Interrupt agent" @click="stop">◼ Stop</button>
        <button type="submit" class="primary" :disabled="!enabled || pending || !text.trim()">{{ pending ? 'Sending…' : '↑ Send' }}</button>
      </div>
    </div>
    <p v-if="error" class="composer-error" role="alert">{{ error }}</p>
    <p v-else class="composer-hint">{{ enabled ? 'Messages go to the running agent. Use the terminal for login and interactive CLI menus.' : 'Connect to a running interactive session to send a message.' }}</p>
  </form>
</template>
<style scoped>
.workspace-composer { width: 100%; max-width: 904px; margin: 0 auto; padding: 8px 32px 12px; box-sizing: border-box; }
.composer-card { padding: 14px; border: 1px solid var(--border-2); border-radius: 18px; background: var(--panel); box-shadow: 0 6px 24px #0002; }
textarea { width: 100%; min-height: 72px; max-height: 240px; resize: vertical; border: 0; background: transparent; padding: 0; font: 14px/1.6 var(--ui); box-shadow: none; }
textarea:focus { outline: none; }
.composer-card:focus-within { border-color: var(--accent); }
.composer-controls { display: flex; align-items: center; gap: 10px; margin-top: 12px; color: var(--muted); font-size: 11px; }
.composer-controls > span:first-child { margin-right: auto; }
.connection, .composer-shortcut { color: var(--muted-3); }
button { padding: 6px 10px; font-size: 12px; border-radius: 9px; }
.composer-hint, .composer-error { margin: 8px 0 0; text-align: center; color: var(--muted-3); font-size: 10px; line-height: 1.5; }
.composer-error { color: var(--danger); }
@media(max-width: 640px) { .workspace-composer { padding-inline: 10px; } .composer-shortcut { display: none; } }
</style>
