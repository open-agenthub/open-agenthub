<script setup>
import { ref, watch } from 'vue'
const props = defineProps({ menu: Object, canAnswer: Boolean, send: Function })
defineEmits(['open-terminal', 'cancel', 'retry', 'continue'])
const draft = ref('')
const pending = ref(false)
const error = ref('')
const copied = ref(false)
watch(() => props.menu, () => { draft.value = ''; error.value = ''; copied.value = false })
async function submit() {
  if (!props.canAnswer || pending.value || !draft.value.trim()) return
  // A code is one line, never terminal control sequences. Keep it out of composer drafts.
  if (/[\x00-\x20\x7f-\x9f]/.test(draft.value.trim()) || draft.value.length > 8192) {
    error.value = 'Paste a single code or credential.'
    return
  }
  pending.value = true
  const value = draft.value.trim()
  draft.value = ''
  try { await props.send(value) } catch { error.value = 'Could not send. Check the terminal connection and try again.' }
  finally { pending.value = false }
}
async function copy() {
  try { await navigator.clipboard.writeText(props.menu.code); copied.value = true }
  catch { error.value = 'Could not copy. Select the device code and copy it.' }
}
</script>
<template>
  <section class="cli-signin" role="status" aria-live="polite" data-cli-signin>
    <strong>{{ menu.status === 'succeeded' ? 'Signed in' : menu.status === 'saved' ? 'Credential saved' : menu.status === 'cancelled' ? 'Sign-in cancelled' : menu.status === 'failed' ? 'Sign-in failed' : 'Sign in to the agent CLI' }}</strong>
    <template v-if="menu.status === 'waiting'">
      <p>{{ menu.mode === 'device' ? 'Open the sign-in page and enter this device code.'
        : menu.mode === 'browser' ? 'Finish signing in in your browser. The CLI is waiting for confirmation.'
          : menu.mode === 'credential' ? 'Enter the credential requested by the CLI.'
            : 'Open the sign-in page, then paste the code here if prompted.' }}</p>
      <a v-if="menu.url && canAnswer" :href="menu.url" target="_blank" rel="noopener noreferrer" referrerpolicy="no-referrer" data-cli-signin-open>Open sign-in page</a>
      <p v-if="menu.code && canAnswer"><code data-cli-device-code>{{ menu.code }}</code> <button type="button" @click="copy">{{ copied ? 'Copied' : 'Copy device code' }}</button></p>
      <form v-if="canAnswer && (menu.mode === 'code' || menu.mode === 'credential')" autocomplete="off" @submit.prevent="submit">
        <label for="cli-signin-code">{{ menu.mode === 'credential' ? 'Credential' : 'Authorization code' }}</label>
        <input id="cli-signin-code" v-model="draft" type="password" autocomplete="off" autocapitalize="off" :spellcheck="false" :disabled="pending" data-cli-signin-code />
        <button type="submit" :disabled="pending || !draft.trim()" data-cli-signin-submit>{{ pending ? 'Sending…' : 'Send to CLI' }}</button>
      </form>
    </template>
    <p v-if="menu.invalid" role="alert">The CLI did not accept the value. Check the terminal and try again.</p>
    <p v-if="menu.status === 'saved'">The CLI saved the credential. It will check it when connecting.</p>
    <p v-if="menu.status === 'failed'">The CLI could not complete sign-in. Check the terminal for details.</p>
    <p v-if="error" role="alert">{{ error }}</p>
    <p class="actions">
      <button v-if="canAnswer && menu.confirm" type="button" @click="$emit('continue')">Continue</button>
      <button v-if="canAnswer && menu.retry" type="button" @click="$emit('retry')">Retry sign-in</button>
      <button type="button" @click="$emit('open-terminal')">Open terminal</button>
      <button v-if="canAnswer && menu.mode === 'code'" type="button" @click="$emit('cancel')">Cancel (Esc)</button>
    </p>
  </section>
</template>
<style scoped>
.cli-signin { margin: 0 auto 8px; width: calc(100% - 64px); max-width: 840px; padding: 14px; border: 1px solid var(--warn); border-radius: 14px; background: var(--panel); font-size: 12px; box-sizing: border-box; }
p { margin: 8px 0; }
a { color: var(--accent); }
form { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; margin-top: 12px; }
input { min-width: 120px; flex: 1; }
code { font-size: 16px; user-select: all; }
.actions { display: flex; gap: 8px; margin-bottom: 0; }
@media(max-width: 640px) { .cli-signin { width: calc(100% - 20px); } }
</style>
