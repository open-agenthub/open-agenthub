<script setup>
import { computed, onMounted, ref, watch } from 'vue'
import { api } from '../api.js'
import { errorText } from '../lib/errors.js'

// Hands the data of the API-key owner to a team-login user (#501, docs/apikey-handover.md).
// Preview first; the confirm button sends the preview's token, so what is applied is what is shown.

const data = ref(null)
const error = ref('')
const from = ref('')
const to = ref('')
const credentials = ref(false)
const preview = ref(null)
const busy = ref(false)
const done = ref(null)

const MOVES = {
  sessions: 'Sessions', projects: 'Projects', skills: 'Skills', mcpServers: 'MCP servers',
  webhooks: 'Webhook triggers', sessionFiles: 'Session files', usageRecords: 'Usage history',
  sessionMessages: 'Messages between sessions', openRequests: 'Open secret/permission requests',
  shares: 'Shares'
}
const CREDENTIALS = {
  apiKeys: 'Provider API keys', sshKey: 'SSH key', gitIdentity: 'Git name, e-mail and known hosts',
  gitPats: 'Git access tokens', gitOAuth: 'Connected Git providers', userSecrets: 'Stored secrets'
}
const credentialLabel = (kind) => CREDENTIALS[kind]
  || (kind.startsWith('providerLogins.') ? `${kind.slice(15).replace(/^./, c => c.toUpperCase())} logins` : kind)

async function load() {
  error.value = ''
  try {
    data.value = await api.adminHandover()
    // After a handover the source just emptied is no longer offered; move on to the next one.
    if (!data.value.sources.some(s => s.owner === from.value)) from.value = data.value.sources[0]?.owner || ''
  } catch (e) { error.value = errorText(e) }
}
onMounted(load)
// A changed choice makes the shown preview, and its token, about something else.
watch([from, to, credentials], () => { preview.value = null })

const visible = computed(() => data.value?.available && (data.value.sources.length || data.value.history.length))
const target = computed(() => data.value?.targets.find(t => t.owner === to.value))
const moves = computed(() => Object.entries(preview.value?.moves || {}).filter(([, n]) => n > 0))

async function runPreview() {
  busy.value = true; error.value = ''; done.value = null
  try { preview.value = await api.previewHandover(from.value, to.value, credentials.value) }
  catch (e) { error.value = errorText(e) }
  finally { busy.value = false }
}

async function apply() {
  busy.value = true; error.value = ''
  try {
    done.value = await api.applyHandover(preview.value.token)
    preview.value = null
    // The opt-in is per handover: the next one starts without credentials again.
    credentials.value = false
    await load()
  } catch (e) { error.value = errorText(e) }
  finally { busy.value = false }
}

function size(bytes) {
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  let n = bytes, i = 0
  while (n >= 1024 && i < units.length - 1) { n /= 1024; i++ }
  return `${i ? n.toFixed(1) : n} ${units[i]}`
}
const when = (d) => { try { return new Date(d).toLocaleString() } catch { return d } }
const total = (counts) => Object.values(counts || {}).reduce((a, b) => a + b, 0)
</script>

<template>
  <section v-if="visible" class="card" data-handover>
    <div class="card-head"><h4>Hand over data from API-key mode</h4></div>
    <p class="note">
      What was created with the API key belongs to its owner name, not to a person. Choose the
      team-login user it belongs to; they must have signed in once. Their own data stays as it is.
    </p>
    <p v-if="error" class="err" data-handover-error>{{ error }}</p>

    <div v-if="data.sources.length" class="form">
      <label>From
        <select v-model="from" data-handover-from>
          <option v-for="s in data.sources" :key="s.owner" :value="s.owner">
            {{ s.owner }} ({{ s.sessions }} sessions, {{ s.projects }} projects)
          </option>
        </select>
      </label>
      <label>To
        <select v-model="to" data-handover-to>
          <option value="" disabled>Choose a user</option>
          <option v-for="t in data.targets" :key="t.owner" :value="t.owner">
            {{ t.displayName ? `${t.displayName} (${t.owner})` : t.owner }}
          </option>
        </select>
      </label>
      <label class="check">
        <input v-model="credentials" type="checkbox" data-handover-credentials />
        <span>Also hand over stored credentials</span>
      </label>
      <p v-if="credentials" class="warn" data-handover-credentials-warning>
        The target user will be able to use these logins and keys (for example someone else's model
        subscription or Git access). Hand them over only if they belong to that person.
      </p>
      <p v-if="!data.targets.length" class="muted">No team-login user has signed in yet.</p>
      <button class="ghost" :disabled="!from || !to || busy" data-handover-preview @click="runPreview">Preview</button>
    </div>

    <div v-if="preview" class="preview" data-handover-result>
      <h5>Moves to {{ target?.displayName || preview.to }}</h5>
      <table>
        <tr v-for="[kind, n] in moves" :key="kind" :data-handover-move="kind"><td>{{ MOVES[kind] || kind }}</td><td>{{ n }}</td></tr>
        <tr v-if="preview.storage.configured" data-handover-storage>
          <td>Stored session data (copied)</td><td>{{ preview.storage.objects }} objects, {{ size(preview.storage.bytes) }}</td>
        </tr>
      </table>
      <p v-if="!moves.length" class="muted">Nothing left to move.</p>
      <p v-for="r in preview.renames" :key="r.kind + r.name + r.scope" class="note" data-handover-rename>
        {{ MOVES[r.kind] || r.kind }} <b>{{ r.name }}</b> becomes <b>{{ r.newName }}</b>: {{ preview.to }} already has one with that name.
      </p>
      <p v-if="preview.scheduledSessions" class="note">
        {{ preview.scheduledSessions }} scheduled session(s) are suspended during the handover and then set up again for {{ preview.to }}.
      </p>

      <h5>Stored credentials</h5>
      <p v-if="!preview.credentials.length" class="muted">{{ preview.from }} has no stored credentials.</p>
      <table v-else>
        <tr v-for="c in preview.credentials" :key="c.kind" :data-handover-credential="c.kind">
          <td>{{ credentialLabel(c.kind) }} ({{ c.count }})</td>
          <td v-if="!preview.includeCredentials">stays with {{ preview.from }}; sessions that need them will ask for new credentials</td>
          <td v-else-if="c.kept">{{ c.count - c.kept }} move; {{ c.kept }} kept by {{ preview.to }}'s own</td>
          <td v-else>moves</td>
        </tr>
      </table>

      <h5>Stays</h5>
      <ul class="stays">
        <li v-if="preview.stays.apiTokens" data-handover-stays="apiTokens">{{ preview.stays.apiTokens }} personal API token(s) stay with {{ preview.from }}; revoke them if nobody needs them.</li>
        <li v-if="preview.stays.chatThreads">{{ preview.stays.chatThreads }} chat thread(s) end; the next question opens a thread in {{ preview.to }}'s chat.</li>
        <li v-if="preview.stays.usageLimits">The usage limit set for {{ preview.from }} stays; set one for {{ preview.to }} if needed.</li>
        <li>The user row and seat of {{ preview.from }} stay; revoke the seat under Users &amp; seats.</li>
      </ul>

      <p v-if="preview.liveSessions.length" class="err" data-handover-live>
        Pause these sessions first: {{ preview.liveSessions.join(', ') }}. A running session keeps writing to {{ preview.from }}'s storage.
      </p>
      <button v-else class="primary" :disabled="busy || !preview.token" data-handover-apply @click="apply">
        Hand over to {{ preview.to }}
      </button>
    </div>

    <p v-if="done" class="ok" data-handover-done>
      Handed over to {{ done.audit.to }}.
      <template v-if="done.cleanupPending"> Removing the old copies did not finish; it is retried on the next preview.</template>
    </p>

    <details v-if="data.history.length" class="audit" data-handover-history>
      <summary>Handovers</summary>
      <div v-for="h in data.history" :key="h.id" class="arow">
        {{ when(h.createdAt) }} · <b>{{ h.admin }}</b>: {{ h.from }} → {{ h.to }} · {{ total(h.counts) }} items<template v-if="h.credentials">, with credentials</template>
        <template v-if="h.state !== 'completed'"> · cleanup pending</template>
      </div>
    </details>
  </section>
</template>

<style scoped>
.card { background: var(--panel); border: 1px solid var(--border-2); border-radius: var(--radius-lg); padding: 18px 20px; margin-bottom: 16px; }
.card-head { display: flex; align-items: center; justify-content: space-between; margin-bottom: 6px; }
.card-head h4 { margin: 0; font-size: 15px; }
h5 { margin: 14px 0 6px; font-size: 13px; }
.note { color: var(--muted); font-size: 12px; line-height: 1.5; margin: 0 0 10px; }
.form { display: flex; flex-wrap: wrap; gap: 10px 16px; align-items: flex-end; }
.form label { display: flex; flex-direction: column; gap: 4px; font-size: 12px; color: var(--muted); }
.form label.check { flex-direction: row; align-items: center; gap: 6px; color: inherit; white-space: nowrap; }
.form label.check input { width: auto; margin: 0; }
.form select { min-width: 200px; max-width: 100%; }
.warn { flex-basis: 100%; margin: 0; font-size: 12px; color: #d9a13c; }
table { border-collapse: collapse; font-size: 12px; width: 100%; }
td { padding: 4px 8px 4px 0; border-top: 1px solid var(--border); vertical-align: top; }
td:last-child { color: var(--muted); }
.stays { margin: 0 0 12px; padding-left: 18px; font-size: 12px; color: var(--muted); }
.audit { margin-top: 12px; font-size: 12px; color: var(--muted); }
.audit summary { cursor: pointer; }
.arow { padding: 4px 0; border-top: 1px solid var(--border); font-family: var(--mono); font-size: 11px; }
.muted { color: var(--muted); font-size: 12px; margin: 0; }
.err { color: var(--danger); font-size: 12px; }
.ok { color: var(--ok); font-size: 12px; }
</style>
