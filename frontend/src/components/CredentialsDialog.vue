<script setup>
import { ref, onMounted } from 'vue'
import { api, config } from '../api.js'
import { docsUrl } from '../lib/docs.js'
import ProviderAccountsPane from './ProviderAccountsPane.vue'

const emit = defineEmits(['close', 'accounts'])
const props = defineProps({ embedded: { type: Boolean, default: false } })
const c = ref({
  sshPrivateKey: '', gitlabToken: '', gitlabHost: '', githubToken: '', githubHost: '',
  anthropicApiKey: '', openAiApiKey: '', cursorApiKey: '',
  gitKnownHosts: '', gitUserName: '', gitUserEmail: ''
})
// Which fields already have a stored value (values are never sent back).
const stored = ref({})
// Fields the user marked for removal.
const clear = ref(new Set())
const busy = ref(false)
const error = ref('')
const saved = ref(false)

async function reloadStatus() {
  try { stored.value = await api.getCredentialStatus() } catch { /* older backend */ }
}
onMounted(reloadStatus)

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
      <p class="note">Written directly to a per-user Kubernetes secret and never read back. Leave fields empty to keep existing values.</p>

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
        <label>SSH private key (for GitLab)
          <button v-if="stored.sshPrivateKey" type="button" class="chip" :class="{ del: clear.has('sshPrivateKey') }"
            data-clear="sshPrivateKey" :aria-label="clear.has('sshPrivateKey') ? 'Keep stored SSH private key' : 'Remove stored SSH private key'"
            @click="toggleClear('sshPrivateKey')">{{ clear.has('sshPrivateKey') ? 'remove ✕' : 'stored ✓ — click to remove' }}</button>
        </label>
        <textarea v-model="c.sshPrivateKey" :placeholder="placeholderFor('sshPrivateKey', '-----BEGIN OPENSSH PRIVATE KEY-----')"></textarea>
      </div>
      <div class="field">
        <label>known_hosts entry (git host)
          <button v-if="stored.gitKnownHosts" type="button" class="chip" :class="{ del: clear.has('gitKnownHosts') }"
            data-clear="gitKnownHosts" :aria-label="clear.has('gitKnownHosts') ? 'Keep stored known_hosts entry' : 'Remove stored known_hosts entry'"
            @click="toggleClear('gitKnownHosts')">{{ clear.has('gitKnownHosts') ? 'remove ✕' : 'stored ✓ — click to remove' }}</button>
        </label>
        <textarea v-model="c.gitKnownHosts" :placeholder="placeholderFor('gitKnownHosts', 'git.example.com ssh-ed25519 AAAA…')"></textarea>
      </div>
      <div class="field">
        <label>GitLab token (for HTTPS remotes, optional)
          <button v-if="stored.gitlabToken" type="button" class="chip" :class="{ del: clear.has('gitlabToken') }"
            data-clear="gitlabToken" :aria-label="clear.has('gitlabToken') ? 'Keep stored GitLab token' : 'Remove stored GitLab token'"
            @click="toggleClear('gitlabToken')">{{ clear.has('gitlabToken') ? 'remove ✕' : 'stored ✓ — click to remove' }}</button>
        </label>
        <input v-model="c.gitlabToken" type="password" :placeholder="placeholderFor('gitlabToken', 'glpat-…')" />
        <input v-model="c.gitlabHost" data-credential="gitlabHost" type="text"
          :placeholder="placeholderFor('gitlabHost', 'gitlab.com (host this token is for)')" />
        <small data-credential-hint="gitlabHost">The token is only ever sent to this host. Write-only.</small>
      </div>
      <div class="field">
        <label>GitHub token (for HTTPS remotes, optional)
          <button v-if="stored.githubToken" type="button" class="chip" :class="{ del: clear.has('githubToken') }"
            data-clear="githubToken" :aria-label="clear.has('githubToken') ? 'Keep stored GitHub token' : 'Remove stored GitHub token'"
            @click="toggleClear('githubToken')">{{ clear.has('githubToken') ? 'remove ✕' : 'stored ✓ — click to remove' }}</button>
        </label>
        <input v-model="c.githubToken" data-credential="githubToken" type="password" autocomplete="off"
          :placeholder="placeholderFor('githubToken', 'ghp_…')" />
        <input v-model="c.githubHost" data-credential="githubHost" type="text"
          :placeholder="placeholderFor('githubHost', 'github.com (host this token is for)')" />
        <small data-credential-hint="githubToken">Only needed when no GitHub provider is connected — a
          connected provider is refreshed automatically and scoped to the repositories you pick. The
          token is only ever sent to the host named above, and also authorizes the gh CLI.</small>
      </div>
      <div class="field">
        <label>Anthropic API key
          <button v-if="stored.anthropicApiKey" type="button" class="chip" :class="{ del: clear.has('anthropicApiKey') }"
            data-clear="anthropicApiKey" :aria-label="clear.has('anthropicApiKey') ? 'Keep stored Anthropic API key' : 'Remove stored Anthropic API key'"
            @click="toggleClear('anthropicApiKey')">{{ clear.has('anthropicApiKey') ? 'remove ✕' : 'stored ✓' }}</button>
        </label>
        <input v-model="c.anthropicApiKey" data-credential="anthropicApiKey" type="password" autocomplete="off"
          :placeholder="placeholderFor('anthropicApiKey', 'sk-ant-…')" />
        <small data-credential-hint="anthropicApiKey">Claude API key billing, or OpenClaw with Anthropic as the API key source. Write-only.</small>
      </div>
      <div class="field">
        <label>OpenAI API key
          <button v-if="stored.openAiApiKey" type="button" class="chip" :class="{ del: clear.has('openAiApiKey') }"
            data-clear="openAiApiKey" :aria-label="clear.has('openAiApiKey') ? 'Keep stored OpenAI API key' : 'Remove stored OpenAI API key'"
            @click="toggleClear('openAiApiKey')">{{ clear.has('openAiApiKey') ? 'remove ✕' : 'stored ✓' }}</button>
        </label>
        <input v-model="c.openAiApiKey" data-credential="openAiApiKey" type="password" autocomplete="off"
          :placeholder="placeholderFor('openAiApiKey', 'sk-…')" />
        <small data-credential-hint="openAiApiKey">Codex API key billing, or OpenClaw with OpenAI as the API key source. Write-only.</small>
      </div>
      <div class="field">
        <label>Cursor API key
          <button v-if="stored.cursorApiKey" type="button" class="chip" :class="{ del: clear.has('cursorApiKey') }"
            data-clear="cursorApiKey" data-credential-status="cursorApiKey"
            :aria-label="clear.has('cursorApiKey') ? 'Keep stored Cursor API key' : 'Remove stored Cursor API key'"
            @click="toggleClear('cursorApiKey')">{{ clear.has('cursorApiKey') ? 'remove ✕' : 'stored ✓' }}</button>
        </label>
        <input v-model="c.cursorApiKey" data-credential="cursorApiKey" type="password" autocomplete="off"
          :placeholder="placeholderFor('cursorApiKey', 'key_…')" />
        <small data-credential-hint="cursorApiKey">Cursor API key billing, or OpenClaw with Cursor as the API key source. Write-only.</small>
      </div>
      <!-- Provider logins live in their own component: they are captured from sessions, never
           typed in here, and there can be several per provider (docs/provider-accounts.md). -->
      <ProviderAccountsPane :status="stored" @changed="reloadStatus" />
      <div class="grid">
        <div class="field">
          <label>Git name
            <button v-if="stored.gitUserName" type="button" class="chip" :class="{ del: clear.has('gitUserName') }"
              data-clear="gitUserName" :aria-label="clear.has('gitUserName') ? 'Keep stored git name' : 'Remove stored git name'"
              @click="toggleClear('gitUserName')">{{ clear.has('gitUserName') ? 'remove ✕' : 'stored ✓' }}</button>
          </label>
          <input v-model="c.gitUserName" :placeholder="placeholderFor('gitUserName', 'Jane Doe')" />
        </div>
        <div class="field">
          <label>Git email
            <button v-if="stored.gitUserEmail" type="button" class="chip" :class="{ del: clear.has('gitUserEmail') }"
              data-clear="gitUserEmail" :aria-label="clear.has('gitUserEmail') ? 'Keep stored git email' : 'Remove stored git email'"
              @click="toggleClear('gitUserEmail')">{{ clear.has('gitUserEmail') ? 'remove ✕' : 'stored ✓' }}</button>
          </label>
          <input v-model="c.gitUserEmail" :placeholder="placeholderFor('gitUserEmail', 'jane@…')" />
        </div>
      </div>

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
.modal { width: 540px; max-width: 100%; background: var(--panel); border: 1px solid var(--border); border-radius: 14px; padding: 22px; }
.modal h3 { margin: 0 0 6px; }
.note { color: var(--muted); font-size: 12px; line-height: 1.5; margin: 0 0 16px; }
.git-hint { display: flex; align-items: center; gap: 14px; border: 1px dashed var(--border-3); border-radius: var(--radius-lg); padding: 12px 16px; margin-bottom: 16px; }
.gh-text { flex: 1; font-size: 13px; color: var(--muted); line-height: 1.5; }
.gh-text b { color: var(--text); }
.gh-text code { font-family: var(--mono); font-size: 12px; color: var(--accent); }
.gh-btn { flex-shrink: 0; }
.grid { display: grid; grid-template-columns: 1fr 1fr; gap: 12px; }
.row { display: flex; justify-content: flex-end; gap: 10px; margin-top: 8px; }
.err { color: var(--danger); font-family: var(--mono); font-size: 12px; }
small { display: block; margin-top: 5px; color: var(--muted-3); font-size: 11px; line-height: 1.4; }
.chip {
  width: auto; margin-left: 8px; font-family: var(--mono); font-size: 10px; cursor: pointer; background: none;
  color: var(--ok); border: 1px solid var(--border); border-radius: 999px; padding: 1px 8px;
}
.chip:hover { border-color: var(--danger); color: var(--danger); }
.chip.del { color: var(--danger); border-color: var(--danger); }
@media (max-width: 760px) { .grid { grid-template-columns: 1fr; } }
</style>
