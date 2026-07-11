<script setup>
import { ref } from 'vue'
import { api } from '../api.js'

const emit = defineEmits(['close'])
const c = ref({
  sshPrivateKey: '', gitlabToken: '', anthropicApiKey: '',
  gitKnownHosts: '', gitUserName: '', gitUserEmail: ''
})
const busy = ref(false)
const error = ref('')
const saved = ref(false)

async function save() {
  busy.value = true; error.value = ''
  try {
    // Only send filled-in fields (avoids overwriting with empty values).
    const payload = Object.fromEntries(Object.entries(c.value).filter(([, v]) => v?.trim()))
    await api.storeCredentials(payload)
    saved.value = true
    setTimeout(() => emit('close'), 800)
  } catch (e) { error.value = String(e.message || e) }
  finally { busy.value = false }
}
</script>

<template>
  <div class="overlay" @click.self="$emit('close')">
    <div class="modal">
      <h3>Credentials</h3>
      <p class="note">Written directly to a per-user Kubernetes secret and never read back. Leave fields empty to keep existing values.</p>

      <div class="field">
        <label>SSH private key (for GitLab)</label>
        <textarea v-model="c.sshPrivateKey" placeholder="-----BEGIN OPENSSH PRIVATE KEY-----"></textarea>
      </div>
      <div class="field">
        <label>known_hosts entry (GitLab host)</label>
        <textarea v-model="c.gitKnownHosts" placeholder="gitlab.example.com ssh-ed25519 AAAA…"></textarea>
      </div>
      <div class="field">
        <label>GitLab token (for HTTPS remotes, optional)</label>
        <input v-model="c.gitlabToken" type="password" placeholder="glpat-…" />
      </div>
      <div class="field">
        <label>Anthropic API key</label>
        <input v-model="c.anthropicApiKey" type="password" placeholder="sk-ant-…" />
      </div>
      <div class="grid">
        <div class="field"><label>Git name</label><input v-model="c.gitUserName" placeholder="Jane Doe" /></div>
        <div class="field"><label>Git email</label><input v-model="c.gitUserEmail" placeholder="jane@…" /></div>
      </div>

      <p v-if="error" class="err">{{ error }}</p>
      <div class="row">
        <button @click="$emit('close')">Close</button>
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
.grid { display: grid; grid-template-columns: 1fr 1fr; gap: 12px; }
.row { display: flex; justify-content: flex-end; gap: 10px; margin-top: 8px; }
.err { color: var(--danger); font-family: var(--mono); font-size: 12px; }
@media (max-width: 760px) { .grid { grid-template-columns: 1fr; } }
</style>
