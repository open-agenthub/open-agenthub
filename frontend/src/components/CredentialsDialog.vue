<script setup>
import { ref, onMounted } from 'vue'
import { api, config } from '../api.js'

const emit = defineEmits(['close', 'accounts'])
const props = defineProps({ embedded: { type: Boolean, default: false } })
const c = ref({
  sshPrivateKey: '', gitlabToken: '', anthropicApiKey: '',
  gitKnownHosts: '', gitUserName: '', gitUserEmail: ''
})
// Which fields already have a stored value (values are never sent back).
const stored = ref({})
// Fields the user marked for removal.
const clear = ref(new Set())
const busy = ref(false)
const error = ref('')
const saved = ref(false)

onMounted(async () => {
  try { stored.value = await api.getCredentialStatus() } catch { /* older backend */ }
})

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
        their accounts here instead of pasting tokens.</div>
      </div>

      <div class="field">
        <label>SSH private key (for GitLab)
          <span v-if="stored.sshPrivateKey" class="chip" :class="{ del: clear.has('sshPrivateKey') }" @click="toggleClear('sshPrivateKey')">{{ clear.has('sshPrivateKey') ? 'remove ✕' : 'stored ✓' }}</span>
        </label>
        <textarea v-model="c.sshPrivateKey" :placeholder="placeholderFor('sshPrivateKey', '-----BEGIN OPENSSH PRIVATE KEY-----')"></textarea>
      </div>
      <div class="field">
        <label>known_hosts entry (GitLab host)
          <span v-if="stored.gitKnownHosts" class="chip" :class="{ del: clear.has('gitKnownHosts') }" @click="toggleClear('gitKnownHosts')">{{ clear.has('gitKnownHosts') ? 'remove ✕' : 'stored ✓' }}</span>
        </label>
        <textarea v-model="c.gitKnownHosts" :placeholder="placeholderFor('gitKnownHosts', 'gitlab.example.com ssh-ed25519 AAAA…')"></textarea>
      </div>
      <div class="field">
        <label>GitLab token (for HTTPS remotes, optional)
          <span v-if="stored.gitlabToken" class="chip" :class="{ del: clear.has('gitlabToken') }" @click="toggleClear('gitlabToken')">{{ clear.has('gitlabToken') ? 'remove ✕' : 'stored ✓' }}</span>
        </label>
        <input v-model="c.gitlabToken" type="password" :placeholder="placeholderFor('gitlabToken', 'glpat-…')" />
      </div>
      <div class="field">
        <label>Anthropic API key
          <span v-if="stored.anthropicApiKey" class="chip" :class="{ del: clear.has('anthropicApiKey') }" @click="toggleClear('anthropicApiKey')">{{ clear.has('anthropicApiKey') ? 'remove ✕' : 'stored ✓' }}</span>
        </label>
        <input v-model="c.anthropicApiKey" type="password" :placeholder="placeholderFor('anthropicApiKey', 'sk-ant-…')" />
      </div>
      <div class="grid">
        <div class="field">
          <label>Git name
            <span v-if="stored.gitUserName" class="chip" :class="{ del: clear.has('gitUserName') }" @click="toggleClear('gitUserName')">{{ clear.has('gitUserName') ? 'remove ✕' : 'stored ✓' }}</span>
          </label>
          <input v-model="c.gitUserName" :placeholder="placeholderFor('gitUserName', 'Jane Doe')" />
        </div>
        <div class="field">
          <label>Git email
            <span v-if="stored.gitUserEmail" class="chip" :class="{ del: clear.has('gitUserEmail') }" @click="toggleClear('gitUserEmail')">{{ clear.has('gitUserEmail') ? 'remove ✕' : 'stored ✓' }}</span>
          </label>
          <input v-model="c.gitUserEmail" :placeholder="placeholderFor('gitUserEmail', 'jane@…')" />
        </div>
      </div>

      <p v-if="error" class="err">{{ error }}</p>
      <div class="row">
        <button v-if="!embedded" @click="$emit('close')">Close</button>
        <button class="primary" :disabled="busy" @click="save">
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
.chip {
  margin-left: 8px; font-family: var(--mono); font-size: 10px; cursor: pointer;
  color: var(--ok); border: 1px solid var(--border); border-radius: 999px; padding: 1px 8px;
}
.chip:hover { border-color: var(--danger); color: var(--danger); }
.chip.del { color: var(--danger); border-color: var(--danger); }
@media (max-width: 760px) { .grid { grid-template-columns: 1fr; } }
</style>
