<script setup>
import { computed, ref, onMounted } from 'vue'
import { api, config } from '../api.js'
import { docsUrl } from '../lib/docs.js'
import ProviderAccountsPane from './ProviderAccountsPane.vue'

const emit = defineEmits(['close', 'accounts'])
const props = defineProps({ embedded: { type: Boolean, default: false } })

// Fields that are staged here and written together on Save. Git tokens are not among them:
// they are a list with their own endpoints (see addPat/removePat below).
const c = ref({
  sshPrivateKey: '', anthropicApiKey: '', openAiApiKey: '', cursorApiKey: '',
  gitKnownHosts: '', gitUserName: '', gitUserEmail: ''
})
// Which fields already have a stored value (values are never sent back).
const stored = ref({})
// Fields the user marked for removal.
const clear = ref(new Set())
const busy = ref(false)
const error = ref('')
const saved = ref(false)

// One row per API-key provider. The field name doubles as the data-* hook the tests use.
const apiKeys = [
  {
    field: 'anthropicApiKey', label: 'Anthropic API key', placeholder: 'sk-ant-…',
    hint: 'Claude with API key billing, or OpenClaw with Anthropic as the API key source.'
  },
  {
    field: 'openAiApiKey', label: 'OpenAI API key', placeholder: 'sk-…',
    hint: 'Codex with API key billing, or OpenClaw with OpenAI as the API key source.'
  },
  {
    field: 'cursorApiKey', label: 'Cursor API key', placeholder: 'key_…',
    hint: 'Cursor with API key billing, or OpenClaw with Cursor as the API key source.'
  }
]

// --- Git personal access tokens -------------------------------------------------------------
const pats = computed(() => stored.value.gitPats || [])
const newPat = ref({ kind: 'gitlab', host: '', token: '' })
const patBusy = ref(false)
const patError = ref('')
// Inline two-step remove (no confirm() popup): the first click arms, the second removes.
const confirmingPat = ref('')
const canAddPat = computed(() => !patBusy.value && newPat.value.host.trim() && newPat.value.token.trim())
const kindLabel = (kind) => (kind === 'github' ? 'GitHub' : 'GitLab')

async function refreshStatus() {
  try { stored.value = await api.getCredentialStatus() } catch { /* older backend */ }
}

onMounted(refreshStatus)

/**
 * Adds a token immediately rather than staging it for Save: the list is keyed by host and a
 * second token for a host that is already listed rotates that entry, so the user needs to see
 * the result of each add on its own — and a token must not sit in a form field waiting on an
 * unrelated Save that may never come.
 */
async function addPat() {
  if (!canAddPat.value) return
  patBusy.value = true; patError.value = ''
  try {
    await api.addGitPat({
      kind: newPat.value.kind, host: newPat.value.host.trim(), token: newPat.value.token.trim()
    })
    newPat.value = { kind: newPat.value.kind, host: '', token: '' }
    await refreshStatus()
  } catch (e) {
    patError.value = e?.message || 'Could not store the token.'
  } finally {
    patBusy.value = false
  }
}

async function removePat(pat) {
  if (confirmingPat.value !== pat.id) { confirmingPat.value = pat.id; return }
  confirmingPat.value = ''
  patBusy.value = true; patError.value = ''
  try {
    await api.deleteGitPat(pat.id)
    await refreshStatus()
  } catch (e) {
    patError.value = e?.message || `Could not remove the token for ${pat.host}.`
  } finally {
    patBusy.value = false
  }
}

function toggleClear(field) {
  const s = new Set(clear.value)
  s.has(field) ? s.delete(field) : s.add(field)
  clear.value = s
}

function placeholderFor(field, fallback) {
  if (clear.value.has(field)) return 'will be removed on save'
  return stored.value[field] ? '•••••• (stored — leave empty to keep)' : fallback
}

async function save() {
  busy.value = true; error.value = ''
  try {
    // Only send filled-in fields; the backend merges, so untouched fields stay.
    const payload = Object.fromEntries(Object.entries(c.value).filter(([, v]) => v?.trim()))
    if (clear.value.size) payload.clear = [...clear.value]
    await api.storeCredentials(payload)
    saved.value = true
    setTimeout(() => emit('close'), 800)
  } catch (e) { error.value = String(e.message || e) }
  finally { busy.value = false }
}
</script>

<template>
  <div :class="embedded ? 'embed' : 'overlay'" @click.self="embedded || $emit('close')">
    <div :class="embedded ? 'embed-inner' : 'modal'">
      <h3>Credentials</h3>
      <p class="note">Everything here is written to a per-user secret and never read back. A field
        left empty keeps its stored value; git tokens and provider logins apply immediately, the
        rest on Save.</p>

      <!-- Git access -->
      <section class="card" data-card="git">
        <div class="card-head">
          <h4>Git access</h4>
          <p class="card-sub">How sessions clone and push.</p>
        </div>

        <div v-if="config.gitEnabled" class="git-hint" data-git-hint="connect">
          <div class="gh-text"><b>Easier for repositories:</b> connect your GitHub/GitLab account — sessions then clone
          and push through it without any pasted tokens.</div>
          <button class="gh-btn" @click="$emit('accounts')">Connect an account →</button>
        </div>
        <div v-else class="git-hint" data-git-hint="helm">
          <div class="gh-text"><b>Tip for admins:</b> account connect (OAuth) is not configured on this instance.
          Register an OAuth app at your GitHub/GitLab and set <code>git.providers</code>
          (clientId/clientSecret) plus <code>git.stateKey</code> in the Helm values — users can then connect
          their accounts here instead of pasting tokens.
          <a :href="docsUrl('git')" target="_blank" rel="noopener">Callback URL and scopes ↗</a></div>
        </div>

        <div class="field">
          <div class="label-row">
            <label for="cred-ssh">SSH private key</label>
            <button v-if="stored.sshPrivateKey" type="button" class="chip" :class="{ del: clear.has('sshPrivateKey') }"
              data-clear="sshPrivateKey" :aria-label="clear.has('sshPrivateKey') ? 'Keep stored SSH private key' : 'Remove stored SSH private key'"
              @click="toggleClear('sshPrivateKey')">{{ clear.has('sshPrivateKey') ? 'remove on save ✕' : 'stored ✓ · remove' }}</button>
          </div>
          <textarea id="cred-ssh" v-model="c.sshPrivateKey" :placeholder="placeholderFor('sshPrivateKey', '-----BEGIN OPENSSH PRIVATE KEY-----')"></textarea>
          <small>For SSH remotes. Write-only.</small>
        </div>
        <div class="field">
          <div class="label-row">
            <label for="cred-known-hosts">known_hosts entry</label>
            <button v-if="stored.gitKnownHosts" type="button" class="chip" :class="{ del: clear.has('gitKnownHosts') }"
              data-clear="gitKnownHosts" :aria-label="clear.has('gitKnownHosts') ? 'Keep stored known_hosts entry' : 'Remove stored known_hosts entry'"
              @click="toggleClear('gitKnownHosts')">{{ clear.has('gitKnownHosts') ? 'remove on save ✕' : 'stored ✓ · remove' }}</button>
          </div>
          <textarea id="cred-known-hosts" v-model="c.gitKnownHosts" :placeholder="placeholderFor('gitKnownHosts', 'git.example.com ssh-ed25519 AAAA…')"></textarea>
          <small>Host key of your git server. Required for SSH clones — host key checking is strict on purpose.</small>
        </div>
        <div class="grid">
          <div class="field">
            <div class="label-row">
              <label for="cred-git-name">Git name</label>
              <button v-if="stored.gitUserName" type="button" class="chip" :class="{ del: clear.has('gitUserName') }"
                data-clear="gitUserName" :aria-label="clear.has('gitUserName') ? 'Keep stored git name' : 'Remove stored git name'"
                @click="toggleClear('gitUserName')">{{ clear.has('gitUserName') ? 'remove on save ✕' : 'stored ✓ · remove' }}</button>
            </div>
            <input id="cred-git-name" v-model="c.gitUserName" :placeholder="placeholderFor('gitUserName', 'Jane Doe')" />
          </div>
          <div class="field">
            <div class="label-row">
              <label for="cred-git-email">Git email</label>
              <button v-if="stored.gitUserEmail" type="button" class="chip" :class="{ del: clear.has('gitUserEmail') }"
                data-clear="gitUserEmail" :aria-label="clear.has('gitUserEmail') ? 'Keep stored git email' : 'Remove stored git email'"
                @click="toggleClear('gitUserEmail')">{{ clear.has('gitUserEmail') ? 'remove on save ✕' : 'stored ✓ · remove' }}</button>
            </div>
            <input id="cred-git-email" v-model="c.gitUserEmail" :placeholder="placeholderFor('gitUserEmail', 'jane@example.com')" />
          </div>
        </div>

        <div class="subsection" data-git-pats>
          <h5>Personal access tokens</h5>
          <small class="lead">For HTTPS remotes when no account is connected. One token per host; a token is only
            ever sent to its host, and it also authorizes <code>gh</code> / <code>glab</code> for that host. Adding a
            token for a host that is already listed replaces it.</small>

          <ul class="pat-list" data-git-pat-list>
            <li v-if="!pats.length" class="pat-empty" data-git-pat-empty>No tokens stored.</li>
            <li v-for="pat in pats" :key="pat.id" class="pat" :data-git-pat="pat.host">
              <span class="pill kind" :class="pat.kind">{{ kindLabel(pat.kind) }}</span>
              <code class="pat-host">{{ pat.host }}</code>
              <span class="pat-stored">stored ✓</span>
              <span class="pat-actions">
                <button v-if="confirmingPat === pat.id" type="button" class="ghost" data-git-pat-keep
                  @click="confirmingPat = ''">Keep</button>
                <button type="button" class="del" :class="{ armed: confirmingPat === pat.id }" data-git-pat-remove
                  :disabled="patBusy" :aria-label="`Remove the ${kindLabel(pat.kind)} token for ${pat.host}`"
                  @click="removePat(pat)">{{ confirmingPat === pat.id ? 'Really remove?' : 'Remove' }}</button>
              </span>
            </li>
          </ul>

          <div class="pat-add">
            <div class="field">
              <label for="pat-kind">Kind</label>
              <select id="pat-kind" v-model="newPat.kind" data-git-pat-kind>
                <option value="gitlab">GitLab</option>
                <option value="github">GitHub</option>
              </select>
            </div>
            <div class="field">
              <label for="pat-host">Host</label>
              <input id="pat-host" v-model="newPat.host" data-git-pat-host type="text" autocomplete="off"
                :placeholder="newPat.kind === 'github' ? 'github.com' : 'gitlab.example.com'" @keyup.enter="addPat" />
            </div>
            <div class="field">
              <label for="pat-token">Token</label>
              <input id="pat-token" v-model="newPat.token" data-git-pat-token type="password" autocomplete="off"
                :placeholder="newPat.kind === 'github' ? 'ghp_…' : 'glpat-…'" @keyup.enter="addPat" />
            </div>
            <div class="field pat-add-btn">
              <button type="button" class="primary" data-git-pat-add :disabled="!canAddPat" @click="addPat">
                {{ patBusy ? 'Adding…' : 'Add' }}
              </button>
            </div>
          </div>
          <small>Hostname with an optional port, no scheme or path. Stored immediately; write-only.</small>
          <p v-if="patError" class="err" data-git-pat-error>{{ patError }}</p>
        </div>
      </section>

      <!-- API keys -->
      <section class="card" data-card="api-keys">
        <div class="card-head">
          <h4>API keys</h4>
          <p class="card-sub">Pay-per-use billing instead of a subscription login. Write-only.</p>
        </div>
        <div v-for="k in apiKeys" :key="k.field" class="field key-row">
          <div class="label-row">
            <label :for="`cred-${k.field}`">{{ k.label }}</label>
            <span v-if="stored[k.field]" class="pill" :class="clear.has(k.field) ? 'pill-del' : 'pill-ok'"
              :data-credential-status="k.field">{{ clear.has(k.field) ? 'removed on save' : 'stored' }}</span>
            <span v-else class="pill pill-off">not set</span>
            <button v-if="stored[k.field]" type="button" class="chip" :class="{ del: clear.has(k.field) }"
              :data-clear="k.field" :aria-label="clear.has(k.field) ? `Keep stored ${k.label}` : `Remove stored ${k.label}`"
              @click="toggleClear(k.field)">{{ clear.has(k.field) ? 'keep' : 'remove ✕' }}</button>
          </div>
          <input :id="`cred-${k.field}`" v-model="c[k.field]" :data-credential="k.field" type="password" autocomplete="off"
            :placeholder="placeholderFor(k.field, k.placeholder)" />
          <small :data-credential-hint="k.field">{{ k.hint }}</small>
        </div>
      </section>

      <!-- Provider logins -->
      <section class="card" data-card="logins">
        <div class="card-head">
          <h4>Provider logins</h4>
          <p class="card-sub">Captured when you sign in during an Interactive session and reused by later sessions.</p>
        </div>
        <!-- Captured from sessions, never typed in here, and there can be several per provider
             (docs/provider-accounts.md); the pane owns listing, default and removal. -->
        <ProviderAccountsPane :status="stored" @changed="refreshStatus" />
      </section>

      <p v-if="error" class="err">{{ error }}</p>
      <div class="row">
        <button v-if="!embedded" @click="$emit('close')">Close</button>
        <button class="primary" data-save-credentials :disabled="busy" @click="save">
          {{ saved ? 'Saved ✓' : busy ? 'Saving…' : 'Save' }}
        </button>
      </div>
    </div>
  </div>
</template>

<style scoped>
.overlay {
  position: fixed; inset: 0; background: rgba(5,7,10,.7);
  display: flex; align-items: flex-start; justify-content: center; padding: 24px; overflow-y: auto; z-index: 50;
}
.modal { width: 600px; max-width: 100%; background: var(--panel); border: 1px solid var(--border); border-radius: 14px; padding: 22px; }
.modal h3 { margin: 0 0 6px; }
.note { color: var(--muted); font-size: 12px; line-height: 1.5; margin: 0 0 16px; }

.card { padding: 16px 18px; margin-bottom: 14px; }
.card-head { margin-bottom: 14px; }
.card-head h4 { margin: 0 0 2px; font-size: 15px; }
.card-sub { margin: 0; color: var(--muted); font-size: 12px; line-height: 1.5; }

.git-hint { display: flex; align-items: center; gap: 14px; border: 1px dashed var(--border-3); border-radius: var(--radius); padding: 12px 14px; margin-bottom: 14px; }
.gh-text { flex: 1; font-size: 13px; color: var(--muted); line-height: 1.5; }
.gh-text b { color: var(--text); }
.gh-text code { font-family: var(--mono); font-size: 12px; color: var(--accent); }
.gh-btn { flex-shrink: 0; }

.label-row { display: flex; align-items: center; gap: 8px; margin: 0 0 6px; }
.label-row label { margin: 0; }
.grid { display: grid; grid-template-columns: 1fr 1fr; gap: 0 12px; }
.row { display: flex; justify-content: flex-end; gap: 10px; margin-top: 8px; }
.err { color: var(--danger); font-family: var(--mono); font-size: 12px; margin: 6px 0 0; }
small { display: block; margin-top: 5px; color: var(--muted-3); font-size: 11px; line-height: 1.4; }
small code { font-family: var(--mono); font-size: 11px; color: var(--muted); }
.chip {
  width: auto; font-family: var(--mono); font-size: 10px; cursor: pointer; background: none;
  color: var(--ok); border: 1px solid var(--border); border-radius: 999px; padding: 1px 8px;
}
.chip:hover { border-color: var(--danger); color: var(--danger); }
.chip.del { color: var(--danger); border-color: var(--danger); }

/* Status pills on the API-key rows. */
.pill-ok { background: rgba(95,214,139,.12); color: var(--ok); }
.pill-off { background: var(--panel-2); color: var(--muted-3); font-weight: 600; }
.pill-del { background: rgba(245,122,106,.12); color: var(--danger); }
.key-row { margin-bottom: 16px; }
.key-row:last-child { margin-bottom: 0; }

/* Tokens subsection inside the git card. */
.subsection { border-top: 1px solid var(--border); margin-top: 4px; padding-top: 14px; }
.subsection h5 { margin: 0 0 4px; font-size: 13px; font-family: var(--ui); color: var(--text); letter-spacing: 0; }
.subsection .lead { margin: 0 0 10px; }
.pat-list { list-style: none; margin: 0 0 12px; padding: 0; display: flex; flex-direction: column; gap: 6px; }
.pat { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; border: 1px solid var(--border); border-radius: 10px; padding: 8px 10px; }
.pat-empty { color: var(--muted); font-size: 12px; padding: 4px 0; }
.pill.kind { background: var(--panel-2); color: var(--muted); }
.pill.kind.gitlab { color: var(--warn); }
.pill.kind.github { color: var(--text); }
.pat-host { font-family: var(--mono); font-size: 12px; color: var(--text); min-width: 0; overflow-wrap: anywhere; }
.pat-stored { font-family: var(--mono); font-size: 10px; color: var(--ok); }
.pat-actions { margin-left: auto; display: flex; gap: 6px; }
.pat-actions button { padding: 4px 10px; font-size: 12px; }
.del { color: var(--danger); border-color: var(--border); }
.del:hover, .del.armed { border-color: var(--danger); }
.pat-add { display: grid; grid-template-columns: 120px 1fr 1fr auto; gap: 10px; align-items: end; }
.pat-add .field { margin-bottom: 0; }
.pat-add-btn button { width: 100%; }

/* Provider login rows (grouped here; a dedicated component replaces them later). */

@media (max-width: 760px) {
  .grid { grid-template-columns: 1fr; }
  .pat-add { grid-template-columns: 1fr 1fr; }
  .pat-add .field:nth-child(3), .pat-add-btn { grid-column: 1 / -1; }
  .git-hint { flex-direction: column; align-items: stretch; }
}
</style>
