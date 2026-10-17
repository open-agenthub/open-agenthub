<script setup>
// Settings → Secrets (docs/session-secrets.md): named strings a session receives as environment
// variables or files in ~/.secrets. Write-only like every credential here: the list shows name,
// delivery and description, and a stored value is never sent back to fill a field.
import { computed, nextTick, onMounted, ref, watch } from 'vue'
import { api } from '../api.js'
import { DELIVERIES, deliveryLabel } from '../lib/secrets.js'

const props = defineProps({
  // A form to open on arrival — name, delivery and purpose an agent asked for; the value is
  // always typed here, never handed in.
  prefill: { type: Object, default: null }
})
const emit = defineEmits(['saved'])

const secrets = ref([])
const loaded = ref(false)
const loadError = ref('')
// null = no form open; otherwise the draft, with `existing` set when an entry is being edited.
const draft = ref(null)
const busy = ref(false)
const error = ref('')
// Inline two-step remove (no confirm() popup): the first click arms, the second removes.
const confirming = ref('')
const valueField = ref(null)
// Open requests of the owner's sessions, the one being answered, and what answering gave.
const requests = ref([])
const answering = ref(null)
const granted = ref(null)
const restartArmed = ref(false)
const restartBusy = ref(false)

const editing = computed(() => !!draft.value?.existing)
const canSave = computed(() => !!draft.value && !busy.value && draft.value.name.trim().length > 0
  && (editing.value || draft.value.value.length > 0))

async function load() {
  loadError.value = ''
  try { secrets.value = (await api.listSecrets()) || [] }
  catch (e) { loadError.value = String(e.message || e) }
  finally { loaded.value = true }
}

function open(base = {}) {
  error.value = ''
  draft.value = {
    name: base.name || '',
    delivery: base.delivery === 'file' ? 'file' : 'env',
    description: base.description || '',
    value: '',
    existing: base.id || null
  }
  nextTick(() => valueField.value?.focus?.())
}

function close() {
  draft.value = null
  error.value = ''
  answering.value = null
}

async function loadRequests() {
  try { requests.value = (await api.listSecretRequests()) || [] } catch { /* older backend */ }
}

// A request for a name that is already stored opens that entry, so the value is replaced (or
// kept) rather than a second secret attempted under the same name.
function openFor(base) {
  const known = secrets.value.find(s => s.name.toLowerCase() === String(base.name || '').toLowerCase())
  open(known ? { ...known, description: base.description || known.description } : base)
}

function answer(request) {
  granted.value = null
  answering.value = request
  openFor({ name: request.name, delivery: request.delivery, description: request.purpose })
}

async function refuse(request) {
  try { await api.refuseSecretRequest(request.id) } catch (e) { loadError.value = String(e.message || e) }
  if (answering.value?.id === request.id) close()
  await loadRequests()
}

// A variable only reaches a container started after the grant. Pausing uploads the session's
// state first, and resuming continues the same conversation, so this is the restart the
// owner confirms — never one the page does on its own.
async function restartSession() {
  if (!restartArmed.value) { restartArmed.value = true; return }
  restartBusy.value = true
  try {
    await api.pauseSession(granted.value.sessionId)
    await api.resumeSession(granted.value.sessionId)
    granted.value = { ...granted.value, restartRequired: false, restarted: true }
  } catch (e) {
    error.value = String(e.message || e)
  } finally {
    restartBusy.value = false
    restartArmed.value = false
  }
}

async function save() {
  if (!canSave.value) return
  busy.value = true
  error.value = ''
  try {
    const saved = await api.upsertSecret({
      name: draft.value.name.trim(),
      delivery: draft.value.delivery,
      // Omitted on an edit with the field left empty: the stored value is kept.
      value: draft.value.value.length ? draft.value.value : null,
      description: draft.value.description.trim() || null
    })
    // The typed value is not kept around in the page once it is stored.
    draft.value = null
    if (answering.value) {
      const request = answering.value
      answering.value = null
      const grant = await api.provideSecretRequest(request.id, saved.id)
      granted.value = { ...grant, sessionId: request.sessionId, sessionTitle: request.sessionTitle }
      restartArmed.value = false
      dropRequestFromUrl()
    }
    await Promise.all([load(), loadRequests()])
    emit('saved', saved)
  } catch (e) {
    error.value = String(e.message || e)
  } finally {
    busy.value = false
  }
}

async function remove(secret) {
  if (confirming.value !== secret.id) { confirming.value = secret.id; return }
  confirming.value = ''
  try { await api.deleteSecret(secret.id); await load() }
  catch (e) { loadError.value = String(e.message || e) }
}

// The link in the notification carries the request; once answered it is taken off the address,
// so a reload does not offer to answer it again.
function requestFromUrl() {
  try { return new URLSearchParams(window.location.search).get('request') } catch { return null }
}
function dropRequestFromUrl() {
  try {
    if (!requestFromUrl()) return
    const url = new URL(window.location.href)
    url.searchParams.delete('request')
    window.history.replaceState(window.history.state, '', url.pathname + url.search + url.hash)
  } catch { /* not in a browser */ }
}

onMounted(async () => {
  await Promise.all([load(), loadRequests()])
  const requested = requestFromUrl()
  if (requested) {
    let request = requests.value.find(r => r.id === requested)
    if (!request) { try { request = await api.getSecretRequest(requested) } catch { request = null } }
    if (request && request.status === 'pending') answer(request)
    else if (request) loadError.value = 'This request has already been answered.'
  } else if (props.prefill) {
    openFor(props.prefill)
  }
})
watch(() => props.prefill, prefill => { if (prefill) open(prefill) })
</script>

<template>
  <div class="secrets-pane" data-secrets-pane>
    <h3>Secrets</h3>
    <p class="note">Named values a session receives without them passing through the conversation: as an
      environment variable, or as a file in <code>~/.secrets</code>. A project selects which of them its
      sessions get, and a session can narrow that. Values are write-only, and masked in uploaded
      scrollback, transcripts and chat messages.</p>

    <section v-if="requests.length" class="card requests" data-secret-requests>
      <h4>Asked for by your sessions</h4>
      <ul class="list">
        <li v-for="request in requests" :key="request.id" class="row" :data-secret-request-row="request.id">
          <code class="name">{{ request.name }}</code>
          <span class="pill">{{ deliveryLabel(request.delivery) }}</span>
          <span class="desc">{{ request.sessionTitle || request.sessionId }}<template v-if="request.purpose"> — {{ request.purpose }}</template></span>
          <span class="actions">
            <button type="button" class="primary" data-secret-request-answer @click="answer(request)">Add it</button>
            <button type="button" data-secret-request-refuse @click="refuse(request)">Refuse</button>
          </span>
        </li>
      </ul>
    </section>

    <div v-if="granted" class="granted" data-secret-granted>
      <b>{{ granted.name }}</b> is now available to “{{ granted.sessionTitle || granted.sessionId }}”
      <template v-if="granted.delivery === 'file'">as <code>~/.secrets/{{ granted.name }}</code> (within about a minute).</template>
      <template v-else-if="granted.restarted">as <code>${{ granted.name }}</code> — the session is restarting.</template>
      <template v-else-if="granted.restartRequired">as <code>${{ granted.name }}</code> from its next start.
        <button type="button" class="restart" :class="{ armed: restartArmed }" :disabled="restartBusy" data-secret-restart
          @click="restartSession">{{ restartBusy ? 'Restarting…' : restartArmed ? 'Really restart the session?' : 'Restart it now' }}</button>
      </template>
      <template v-else>as <code>${{ granted.name }}</code>.</template>
    </div>

    <section class="card">
      <ul class="list" data-secret-list>
        <li v-if="loaded && !secrets.length && !loadError" class="empty" data-secret-empty>No secrets stored.</li>
        <li v-for="secret in secrets" :key="secret.id" class="row" :data-secret="secret.name">
          <code class="name">{{ secret.name }}</code>
          <span class="pill">{{ deliveryLabel(secret.delivery) }}</span>
          <span v-if="secret.description" class="desc">{{ secret.description }}</span>
          <span class="actions">
            <button type="button" data-secret-edit @click="open(secret)">Change</button>
            <button v-if="confirming === secret.id" type="button" class="ghost" data-secret-keep @click="confirming = ''">Keep</button>
            <button type="button" class="del" :class="{ armed: confirming === secret.id }" data-secret-remove
              :aria-label="`Remove the secret ${secret.name}`" @click="remove(secret)">{{ confirming === secret.id ? 'Really remove?' : 'Remove' }}</button>
          </span>
        </li>
      </ul>
      <p v-if="loadError" class="err" data-secret-load-error>{{ loadError }}</p>

      <div v-if="draft" class="form" data-secret-form>
        <p v-if="answering" class="answering" data-secret-answering>For “{{ answering.sessionTitle || answering.sessionId }}”,
          which asked for it<template v-if="answering.purpose">: {{ answering.purpose }}</template>. Saving gives it to that session only.</p>
        <div class="grid">
          <div class="field">
            <label for="secret-name">Name</label>
            <input id="secret-name" v-model="draft.name" data-secret-name autocomplete="off" spellcheck="false"
              :readonly="editing" :placeholder="draft.delivery === 'file' ? 'service-account.json' : 'DEPLOY_TOKEN'" />
          </div>
          <div class="field">
            <label for="secret-delivery">Delivered as</label>
            <select id="secret-delivery" v-model="draft.delivery" data-secret-delivery>
              <option v-for="d in DELIVERIES" :key="d.value" :value="d.value">{{ d.label }}</option>
            </select>
          </div>
        </div>
        <div class="field">
          <label for="secret-value">Value <span v-if="editing" class="dim">— leave empty to keep the stored value</span></label>
          <textarea v-if="draft.delivery === 'file'" id="secret-value" ref="valueField" v-model="draft.value"
            data-secret-value autocomplete="off" spellcheck="false" rows="4"></textarea>
          <input v-else id="secret-value" ref="valueField" v-model="draft.value" data-secret-value type="password"
            autocomplete="new-password" spellcheck="false" />
        </div>
        <div class="field">
          <label for="secret-description">What it is for <span class="dim">— optional, shown to you and in the session dialog</span></label>
          <input id="secret-description" v-model="draft.description" data-secret-description autocomplete="off" />
        </div>
        <small>Variable names are letters, digits and <code>_</code>; names the session runtime sets itself
          (<code>AGENTHUB_*</code>, <code>HOME</code>, <code>PATH</code>, provider variables such as
          <code>ANTHROPIC_*</code>) are refused. File names may not start with a dot. Up to 16 KiB each.
          A running session gets a changed file within about a minute, a changed variable at its next start.</small>
        <p v-if="error" class="err" data-secret-error>{{ error }}</p>
        <div class="buttons">
          <button type="button" class="primary" data-secret-save :disabled="!canSave" @click="save">{{ busy ? 'Saving…' : editing ? 'Save' : 'Add secret' }}</button>
          <button type="button" data-secret-cancel @click="close">Cancel</button>
        </div>
      </div>
      <button v-else type="button" class="add" data-secret-add @click="open()">+ Add secret</button>
    </section>
  </div>
</template>

<style scoped>
.secrets-pane { max-width: 760px; }
.note { margin: 0 0 16px; color: var(--muted-2); font-size: 12px; line-height: 1.5; }
.note code, small code { font-family: var(--mono); font-size: 11px; color: var(--muted); }
.card { padding: 16px 18px; }
.list { list-style: none; margin: 0 0 12px; padding: 0; display: flex; flex-direction: column; gap: 6px; }
.empty { color: var(--muted); font-size: 12px; padding: 4px 0; }
.row { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; border: 1px solid var(--border); border-radius: 10px; padding: 8px 10px; }
.name { font-family: var(--mono); font-size: 12px; color: var(--text); overflow-wrap: anywhere; }
.pill { padding: 1px 6px; border: 1px solid var(--border-2); border-radius: 4px; color: var(--muted-3); font-size: 11px; font-weight: 700; }
.desc { color: var(--muted-2); font-size: 12px; min-width: 0; overflow-wrap: anywhere; }
.actions { margin-left: auto; display: flex; gap: 6px; }
.actions button { padding: 4px 10px; font-size: 12px; }
.del { color: var(--danger); border-color: var(--border); }
.del:hover, .del.armed { border-color: var(--danger); }
.form { border-top: 1px solid var(--border); padding-top: 14px; }
.grid { display: grid; grid-template-columns: 1fr 1fr; gap: 0 12px; }
.dim { color: var(--faint); font-weight: 400; }
small { display: block; margin: 0 0 10px; color: var(--muted-3); font-size: 11px; line-height: 1.4; }
.err { color: var(--danger); font-family: var(--mono); font-size: 12px; margin: 6px 0; }
.buttons { display: flex; gap: 10px; }
.requests { margin-bottom: 14px; border-color: var(--warn); }
.requests h4 { margin: 0 0 10px; font-size: 13px; }
.answering { margin: 0 0 10px; color: var(--muted); font-size: 12px; line-height: 1.5; }
.granted { margin: 0 0 14px; padding: 10px 12px; border: 1px solid var(--ok); border-radius: 10px; font-size: 13px; color: var(--text); }
.granted code { font-family: var(--mono); font-size: 12px; }
.restart { margin-left: 8px; padding: 3px 10px; font-size: 12px; }
.restart.armed { border-color: var(--warn); color: var(--warn); }
.add { border: 0; background: none; color: var(--muted); font-size: 12px; font-weight: 600; padding: 4px 0; }
.add:hover { color: var(--accent); background: none; }
@media (max-width: 760px) { .grid { grid-template-columns: 1fr; } }
</style>
