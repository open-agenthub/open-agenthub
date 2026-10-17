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
    await load()
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

onMounted(async () => {
  await load()
  if (props.prefill) {
    // A request for a name that is already stored opens that entry, so the value is replaced
    // rather than a second secret attempted under the same name.
    const known = secrets.value.find(s => s.name.toLowerCase() === String(props.prefill.name || '').toLowerCase())
    open(known ? { ...known, description: props.prefill.description || known.description } : props.prefill)
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
.add { border: 0; background: none; color: var(--muted); font-size: 12px; font-weight: 600; padding: 4px 0; }
.add:hover { color: var(--accent); background: none; }
@media (max-width: 760px) { .grid { grid-template-columns: 1fr; } }
</style>
