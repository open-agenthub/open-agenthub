<script setup>
import { computed, ref, watch } from 'vue'
// The CLI's own sign-in, step by step in the CLI's words (#344). Each step has one primary
// action, and only the step the person is on shows it filled: the sign-in page until it has been
// opened, then the code field's Send. The terminal, Cancel and Copy are links underneath. Before,
// every action was a button of the same weight — up to five at once — and the page itself, the
// first thing to do, was the one plain link among them.
const props = defineProps({ menu: Object, canAnswer: Boolean, send: Function, agent: String })
defineEmits(['open-terminal', 'cancel', 'retry', 'continue'])
const draft = ref('')
const pending = ref(false)
const error = ref('')
const copied = ref(false)
const opened = ref(false)
watch(() => props.menu, () => { draft.value = ''; error.value = ''; copied.value = false })
// A new address is a new sign-in (Retry, or /login again): its page has not been opened yet.
watch(() => props.menu?.url, () => { opened.value = false })
const cli = computed(() => ({ Claude: 'Claude Code', Codex: 'Codex', Cursor: 'Cursor', OpenCode: 'OpenCode', OpenClaw: 'OpenClaw' })[props.agent] || 'the agent CLI')
const title = computed(() => ({
  succeeded: `Signed in to ${cli.value}`, saved: 'Credential saved', cancelled: 'Sign-in cancelled', failed: 'Sign-in failed'
})[props.menu.status] || `Sign in to ${cli.value}`)
const waiting = computed(() => props.menu.status === 'waiting')
const field = computed(() => props.canAnswer && (props.menu.mode === 'code' || props.menu.mode === 'credential'))
// The page comes first wherever there is one; the field's Send takes over once it was opened, or
// as soon as something is pasted (the person may have opened the address another way).
const pagePrimary = computed(() => !opened.value && !draft.value.trim())
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
    <strong class="title">{{ title }}</strong>
    <ol v-if="waiting && canAnswer && (menu.url || field)" class="steps">
      <li v-if="menu.url" :class="{ done: opened }">
        <span>{{ menu.mode === 'device' ? 'Open this link in your browser and sign in to your account.' : 'Open the sign-in page and sign in.' }}</span>
        <a :href="menu.url" target="_blank" rel="noopener noreferrer" referrerpolicy="no-referrer" class="action" :class="{ primary: pagePrimary }"
          data-cli-signin-open @click="opened = true">{{ opened ? 'Open again' : 'Open sign-in page' }}</a>
      </li>
      <li v-if="menu.mode === 'device' && menu.code">
        <span>Enter this one-time code there:</span>
        <span class="code-row"><code data-cli-device-code>{{ menu.code }}</code>
          <button type="button" class="link" data-cli-signin-copy @click="copy">{{ copied ? 'Copied' : 'Copy' }}</button></span>
      </li>
      <li v-if="field">
        <form autocomplete="off" @submit.prevent="submit">
          <label for="cli-signin-code">{{ menu.mode === 'credential' ? 'Enter the credential the CLI asks for.' : 'Paste the code the page shows.' }}</label>
          <span class="field">
            <input id="cli-signin-code" v-model="draft" type="password" autocomplete="off" autocapitalize="off" :spellcheck="false" :disabled="pending" data-cli-signin-code />
            <button type="submit" :class="{ primary: menu.url ? !pagePrimary : true }" :disabled="pending || !draft.trim()" data-cli-signin-submit>{{ pending ? 'Sending…' : menu.mode === 'credential' ? 'Send' : 'Send code' }}</button>
          </span>
        </form>
      </li>
    </ol>
    <p v-if="waiting && (menu.mode === 'device' || menu.mode === 'browser')" class="note">The CLI continues by itself once you have signed in.</p>
    <p v-if="waiting && !canAnswer" class="note">The agent CLI is waiting for its owner to sign in.</p>
    <p v-if="menu.invalid" role="alert">The CLI did not accept the value. Check it and try again.</p>
    <p v-if="menu.status === 'succeeded' && !menu.confirm" class="note">The CLI continues by itself.</p>
    <p v-if="menu.status === 'saved'" class="note">The CLI saved the credential. It will check it when connecting.</p>
    <p v-if="menu.status === 'failed'">The CLI could not complete sign-in.</p>
    <p v-if="error" role="alert">{{ error }}</p>
    <p v-if="canAnswer && (menu.confirm || menu.retry)" class="outcome">
      <button v-if="menu.confirm" type="button" class="primary" data-cli-signin-continue @click="$emit('continue')">Continue</button>
      <button v-else type="button" class="primary" data-cli-signin-retry @click="$emit('retry')">Try again</button>
    </p>
    <p class="secondary">
      <button type="button" class="link" data-cli-open-terminal @click="$emit('open-terminal')">Open terminal</button>
      <button v-if="canAnswer && menu.mode === 'code'" type="button" class="link" data-cli-signin-cancel @click="$emit('cancel')">{{ menu.cancel === 'interrupt' ? 'Cancel (Ctrl+C)' : 'Cancel (Esc)' }}</button>
    </p>
  </section>
</template>
<style scoped>
.cli-signin { margin: 0 auto 8px; width: calc(100% - 64px); max-width: 840px; padding: 14px; border: 1px solid var(--warn); border-radius: 14px; background: var(--panel); font-size: 12px; box-sizing: border-box; }
.title { display: block; font-size: 13px; color: var(--strong); }
p { margin: 8px 0; }
.steps { margin: 10px 0 0; padding-left: 20px; display: flex; flex-direction: column; gap: 10px; }
.steps li { padding-left: 2px; }
.steps li > span:first-child, .steps label { display: block; margin-bottom: 6px; color: var(--text); }
.steps li.done > span:first-child { color: var(--muted); }
/* The sign-in page is a link (it opens in a new tab without an opener), drawn as the step's button. */
.action { display: inline-block; padding: 7px 14px; border: 1px solid var(--border-3); border-radius: var(--radius); color: var(--text); font-weight: 600; text-decoration: none; }
.action:hover { background: var(--panel-2); color: var(--strong); }
.action.primary { background: var(--accent); border-color: var(--accent); color: var(--on-accent); font-weight: 700; }
.action.primary:hover { background: var(--accent-2); border-color: var(--accent-2); }
.action:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
.code-row { display: flex; align-items: center; gap: 10px; }
code { font-size: 16px; user-select: all; }
form { margin: 0; }
.field { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; }
input { min-width: 120px; flex: 1; }
.note { color: var(--muted); }
.outcome { margin-top: 10px; }
.secondary { display: flex; flex-wrap: wrap; gap: 4px; margin: 10px 0 0; color: var(--muted-3); font-size: 11px; }
.link { border: 0; background: none; padding: 0 4px; color: var(--accent); font-size: 11px; font-weight: 500; text-decoration: underline; }
.code-row .link { font-size: 12px; }
.secondary .link:first-child { padding-left: 0; }
@media(max-width: 640px) { .cli-signin { width: calc(100% - 20px); } }
</style>
