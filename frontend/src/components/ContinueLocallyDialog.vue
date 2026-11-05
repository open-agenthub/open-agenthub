<script setup>
import { computed, ref, watch } from 'vue'
import { api } from '../api.js'

// Continue a Claude Code session on the owner's computer and hand it back (#453,
// docs/session-transfer.md). A browser cannot write into ~/.claude, so the hub answers with a
// single-use link and a curl | tar command; the person pastes it into a terminal.
const props = defineProps({ session: Object })
const emit = defineEmits(['close', 'changed'])

const storageKey = computed(() => `agenthub.localDirectory.${props.session.id}`)
// Remembered per session in this browser: the hand-back needs the directory the take-local used,
// and retyping it a day later is where a typo files the upload under a folder that is empty.
const directory = ref(read(storageKey.value))
const shell = ref(/Win/i.test(globalThis.navigator?.platform || '') ? 'powershell' : 'posix')
const resume = ref(true)
const busy = ref('')
const error = ref('')
const taken = ref(null)
const handBack = ref(null)
const copied = ref('')

const live = computed(() => ['Running', 'Pending'].includes(props.session.phase))
const absolute = computed(() => /^(\/|[A-Za-z]:[\\/])/.test(directory.value.trim()))
const command = link => (shell.value === 'powershell' ? link?.powerShellCommand : link?.command) || ''
const until = link => link ? new Date(link.expiresAt).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }) : ''

watch(directory, value => { try { localStorage.setItem(storageKey.value, value) } catch { /* private mode */ } })

function read(key) {
  try { return localStorage.getItem(key) || '' } catch { return '' }
}

async function takeLocal() {
  busy.value = 'take'; error.value = ''; handBack.value = null
  try {
    taken.value = await api.takeSessionLocal(props.session.id, { directory: directory.value.trim(), confirm: true })
    if (taken.value.paused) emit('changed')
  } catch (e) { error.value = e.message || String(e) }
  finally { busy.value = '' }
}

async function createHandBack() {
  busy.value = 'back'; error.value = ''
  try { handBack.value = await api.sessionHandBackLink(props.session.id, { directory: directory.value.trim(), resume: resume.value }) }
  catch (e) { error.value = e.message || String(e) }
  finally { busy.value = '' }
}

async function copy(which, text) {
  try { await navigator.clipboard.writeText(text); copied.value = which }
  catch { copied.value = '' }
}
</script>

<template>
  <div class="local" data-continue-locally>
    <p class="intro">Continue this conversation in Claude Code on your computer, then hand it back.
      Each command uses a single-use link that expires after 15 minutes and needs only <code>curl</code> and <code>tar</code>.</p>

    <label for="local-dir">Local project directory</label>
    <input id="local-dir" v-model="directory" data-local-directory spellcheck="false"
      placeholder="/home/me/project   or   C:\Users\me\project" />
    <p v-if="directory && !absolute" class="hint warn">Use the absolute path: Claude Code finds a conversation by the exact directory it is started in.</p>
    <p v-else class="hint">Usually your checkout of the session’s repository. The command creates it if it is missing.</p>

    <div class="shells" role="radiogroup" aria-label="Shell">
      <label><input v-model="shell" type="radio" value="posix" /> bash / zsh</label>
      <label><input v-model="shell" type="radio" value="powershell" /> PowerShell</label>
    </div>

    <section>
      <h4>1 · Take it local</h4>
      <p v-if="live" class="hint" data-pause-warning>This pauses the session: its pod saves the conversation and stops, so it is never answered in two places.</p>
      <button class="primary" data-take-local :disabled="!absolute || !!busy" @click="takeLocal">
        {{ busy === 'take' ? (live ? 'Pausing…' : 'Preparing…') : live ? 'Pause and create command' : 'Create command' }}</button>
      <template v-if="taken?.link">
        <pre data-take-command>{{ command(taken.link) }}</pre>
        <div class="row">
          <button data-copy-take @click="copy('take', command(taken.link))">{{ copied === 'take' ? 'Copied' : 'Copy' }}</button>
          <span class="hint">Single use, valid until {{ until(taken.link) }}. It ends with <code>claude --resume {{ taken.resumeId }}</code>.</span>
        </div>
      </template>
    </section>

    <section>
      <h4>2 · Hand it back</h4>
      <p class="hint">Quit Claude Code on your computer first, so the conversation is complete. The session must be paused here.</p>
      <label class="check"><input v-model="resume" type="checkbox" data-hand-back-resume /> Resume the session after the upload</label>
      <button data-hand-back :disabled="!absolute || !!busy || live" @click="createHandBack">
        {{ busy === 'back' ? 'Preparing…' : 'Create hand-back command' }}</button>
      <template v-if="handBack?.link">
        <pre data-hand-back-command>{{ command(handBack.link) }}</pre>
        <div class="row">
          <button data-copy-back @click="copy('back', command(handBack.link))">{{ copied === 'back' ? 'Copied' : 'Copy' }}</button>
          <span class="hint">Single use, valid until {{ until(handBack.link) }}. Stop using the local copy afterwards.</span>
        </div>
      </template>
    </section>

    <p v-if="error" role="alert" data-transfer-error>{{ error }}</p>
    <p class="hint">With the AgentHub MCP server in your local Claude Code, ask it instead:
      “take session {{ session.id }} local into my project” — the <code>session_take_local</code> and <code>session_hand_back</code> tools do these steps for you.</p>
  </div>
</template>

<style scoped>
.local { padding: 12px 20px 18px; display: flex; flex-direction: column; gap: 8px; }
.intro { margin: 0; color: var(--muted-2); }
label { font-size: 12px; color: var(--muted-2); }
input#local-dir { width: 100%; font: 13px var(--mono); }
.shells { display: flex; gap: 16px; }
section { border-top: 1px solid var(--border-2); padding-top: 10px; display: flex; flex-direction: column; gap: 8px; align-items: flex-start; }
h4 { margin: 0; font-family: var(--display); color: var(--strong); }
pre { width: 100%; margin: 0; padding: 10px; overflow-x: auto; white-space: pre-wrap; word-break: break-all; font: 12px/1.5 var(--mono); background: var(--bg); border: 1px solid var(--border-2); border-radius: 8px; }
.row { display: flex; gap: 10px; align-items: center; flex-wrap: wrap; }
.hint { margin: 0; font-size: 12px; color: var(--muted-3); }
.warn { color: var(--warn, #d9a441); }
.check { display: flex; gap: 6px; align-items: center; }
[role="alert"] { margin: 0; color: var(--danger, #e5534b); }
</style>
