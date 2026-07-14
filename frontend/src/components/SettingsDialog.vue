<script setup>
import { ref, computed, onMounted } from 'vue'
import { api, config } from '../api.js'

const emit = defineEmits(['close'])
const props = defineProps({ embedded: { type: Boolean, default: false } })

// --- Slack (per-user) ---
const slackEnabled = computed(() => config.slackEnabled)
const slack = ref({ enabled: true, channelOverride: '', email: null, connected: false })
const slackBusy = ref(false)
const slackSaved = ref(false)

async function loadSlack() {
  if (!config.slackEnabled) return
  try { slack.value = await api.slackMe() } catch { /* leave defaults */ }
}
async function saveSlack() {
  slackBusy.value = true; slackSaved.value = false
  try {
    await api.setSlackPrefs({ enabled: slack.value.enabled, channelOverride: slack.value.channelOverride || null })
    slackSaved.value = true
    await loadSlack()
  } catch (e) { error.value = String(e.message || e) }
  finally { slackBusy.value = false }
}

const tokens = ref([])
const loading = ref(true)
const error = ref('')

const newName = ref('')
const creating = ref(false)
// The full token is available only right after creation; shown once, then discarded.
const freshToken = ref(null)
const copied = ref(false)

const origin = location.origin

async function load() {
  loading.value = true; error.value = ''
  try { tokens.value = await api.listApiTokens() }
  catch (e) { error.value = String(e.message || e) }
  finally { loading.value = false }
}

onMounted(() => { load(); loadSlack() })

async function create() {
  const name = newName.value.trim()
  if (!name) return
  creating.value = true; error.value = ''
  try {
    const res = await api.createApiToken(name)
    freshToken.value = res.token
    copied.value = false
    newName.value = ''
    await load()
  } catch (e) { error.value = String(e.message || e) }
  finally { creating.value = false }
}

async function remove(t) {
  if (!confirm(`Delete token "${t.name}"? Any remote clients using it will stop working.`)) return
  try { await api.deleteApiToken(t.id); await load() }
  catch (e) { error.value = String(e.message || e) }
}

async function copyToken() {
  try { await navigator.clipboard.writeText(freshToken.value); copied.value = true }
  catch { /* clipboard unavailable — user can select manually */ }
}

function fmt(d) {
  if (!d) return '—'
  try { return new Date(d).toLocaleString() } catch { return d }
}

const curlCreate = computed(() =>
  `curl -X POST ${origin}/api/remote/sessions \\\n`
  + `  -H "Authorization: Bearer <token>" \\\n`
  + `  -H "Content-Type: application/json" \\\n`
  + `  -d '{"title":"Remote task","mode":"Autonomous","prompt":"..."}'`)

const curlStatus = computed(() =>
  `curl ${origin}/api/remote/sessions/<id> \\\n`
  + `  -H "Authorization: Bearer <token>"`)
</script>

<template>
  <div :class="embedded ? 'embed' : 'overlay'" @click.self="embedded || $emit('close')">
    <div :class="embedded ? 'embed-inner' : 'modal'">
      <h3>Settings</h3>

      <section v-if="slackEnabled" class="slack">
        <h4>Slack notifications</h4>
        <p class="note">
          When a session waits for your input, you get a Slack thread and can answer from there.
          By default we message you directly, resolved from your account email<span v-if="slack.email"> ({{ slack.email }})</span>.
          <span :class="slack.connected ? 'ok-text' : 'muted'">
            {{ slack.connected ? '✓ connected' : 'not connected — no matching Slack user, set a channel below' }}
          </span>
        </p>
        <label class="check">
          <input type="checkbox" v-model="slack.enabled" /> Send me Slack notifications
        </label>
        <div class="field">
          <label>Channel override (optional — a channel/DM id like C0123ABCD; leave empty to use your DM)</label>
          <input v-model="slack.channelOverride" placeholder="auto (your Slack DM)" :disabled="!slack.enabled" />
        </div>
        <div class="slack-actions">
          <button class="primary" :disabled="slackBusy" @click="saveSlack">{{ slackSaved ? 'Saved ✓' : slackBusy ? 'Saving…' : 'Save' }}</button>
        </div>
      </section>

      <h4>API tokens</h4>
      <p class="note">
        Personal API tokens let you start sessions and query their status from outside the UI.
        A token acts on your behalf. The full value is shown only once at creation — store it safely.
      </p>

      <div class="create">
        <input v-model="newName" placeholder="Token name (e.g. laptop, ci-runner)"
          @keyup.enter="create" :disabled="creating" />
        <button class="primary" :disabled="creating || !newName.trim()" @click="create">
          {{ creating ? 'Creating…' : 'Create token' }}
        </button>
      </div>

      <div v-if="freshToken" class="fresh">
        <p class="fresh-label">New token — copy it now, it will not be shown again:</p>
        <code class="fresh-value">{{ freshToken }}</code>
        <button @click="copyToken">{{ copied ? 'Copied ✓' : 'Copy' }}</button>
      </div>

      <p v-if="error" class="err">{{ error }}</p>

      <div class="list">
        <p v-if="loading" class="muted">Loading…</p>
        <p v-else-if="!tokens.length" class="muted">No tokens yet.</p>
        <div v-for="t in tokens" :key="t.id" class="token">
          <div class="token-main">
            <span class="token-name">{{ t.name }}</span>
            <code class="token-prefix">{{ t.prefix }}…</code>
          </div>
          <div class="token-meta">
            <span>created {{ fmt(t.createdAt) }}</span>
            <span>last used {{ fmt(t.lastUsedAt) }}</span>
          </div>
          <button class="del" @click="remove(t)">Delete</button>
        </div>
      </div>

      <details class="help">
        <summary>Using a token from the command line</summary>
        <p class="muted">Start a session:</p>
        <pre>{{ curlCreate }}</pre>
        <p class="muted">Check its status (use the <code>id</code> from the response):</p>
        <pre>{{ curlStatus }}</pre>
      </details>

      <div class="row">
        <button v-if="!embedded" @click="$emit('close')">Close</button>
      </div>
    </div>
  </div>
</template>

<style scoped>
.overlay {
  position: fixed; inset: 0; background: rgba(5,7,10,.7);
  display: flex; align-items: flex-start; justify-content: center; padding: 24px; overflow-y: auto; z-index: 50;
}
.modal { width: 620px; max-width: 100%; background: var(--panel); border: 1px solid var(--border); border-radius: 14px; padding: 22px; }
.modal h3 { margin: 0 0 6px; }
.note { color: var(--muted); font-size: 12px; line-height: 1.5; margin: 0 0 16px; }
.create { display: flex; gap: 10px; margin-bottom: 12px; }
.create input { flex: 1; }
.fresh { background: rgba(0,0,0,.25); border: 1px solid var(--accent); border-radius: 10px; padding: 12px; margin-bottom: 14px; display: flex; flex-wrap: wrap; align-items: center; gap: 10px; }
.fresh-label { margin: 0; flex-basis: 100%; font-size: 12px; color: var(--accent); }
.fresh-value { font-family: var(--mono); font-size: 12px; word-break: break-all; flex: 1; min-width: 200px; }
.list { display: flex; flex-direction: column; gap: 8px; margin-bottom: 16px; }
.token { display: flex; align-items: center; gap: 12px; border: 1px solid var(--border); border-radius: 10px; padding: 10px 12px; }
.token-main { display: flex; flex-direction: column; gap: 2px; min-width: 0; }
.token-name { font-weight: 600; }
.token-prefix { font-family: var(--mono); font-size: 11px; color: var(--muted); }
.token-meta { margin-left: auto; display: flex; flex-direction: column; gap: 2px; text-align: right; font-size: 11px; color: var(--muted); font-family: var(--mono); }
.del { color: var(--danger); border-color: var(--border); }
.del:hover { border-color: var(--danger); }
.help { margin-bottom: 12px; }
.help summary { cursor: pointer; color: var(--muted); font-size: 13px; }
.help pre { background: rgba(0,0,0,.3); border: 1px solid var(--border); border-radius: 8px; padding: 10px; overflow-x: auto; font-family: var(--mono); font-size: 11px; }
.help .muted { margin: 10px 0 4px; }
.muted { color: var(--muted); font-size: 12px; }
.ok-text { color: var(--ok); font-size: 12px; }
.slack { border-bottom: 1px solid var(--border); padding-bottom: 16px; margin-bottom: 16px; }
.slack h4, .modal h4 { margin: 0 0 6px; font-size: 14px; }
.slack .field { margin: 10px 0; display: flex; flex-direction: column; gap: 4px; }
.slack .field label { font-size: 12px; color: var(--muted); }
.slack .check { display: flex; align-items: center; gap: 8px; font-size: 13px; }
.slack .check input { width: auto; }
.slack-actions { display: flex; justify-content: flex-end; }
.row { display: flex; justify-content: flex-end; gap: 10px; margin-top: 8px; }
.err { color: var(--danger); font-family: var(--mono); font-size: 12px; }
</style>
