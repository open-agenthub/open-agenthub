<script setup>
import { onMounted, ref } from 'vue'
import { api, config } from '../api.js'

// Webhook triggers: a GitLab/GitHub webhook (new MR/PR) starts an autonomous session.
// The delivery URL + secret are shown exactly once after creation (inline, no popup).

const EVENT_OPTIONS = ['opened', 'reopened', 'updated', 'closed', 'merged']
const DEFAULT_TEMPLATE = `Review {{repo}} {{id}} ({{action}}): {{title}}

{{description}}

The branch {{source_branch}} is checked out in the workspace. Review the changes against {{target_branch}} and post your findings. Details: {{url}}`

const triggers = ref([])
const providers = ref([])
const loading = ref(true)
const error = ref('')

const name = ref('')
const providerId = ref('')
const events = ref(['opened', 'reopened'])
const repoFilter = ref('')
const promptTemplate = ref(DEFAULT_TEMPLATE)
const agent = ref('Claude')
const autoApprove = ref(false)
const creating = ref(false)

// { trigger, secret } from the last create — the secret is never retrievable again.
const fresh = ref(null)
const copiedUrl = ref(false)
const copiedSecret = ref(false)

// Inline two-step delete (no confirm() popup): first click arms, second deletes.
const confirmingId = ref('')

async function load() {
  loading.value = true; error.value = ''
  try { triggers.value = await api.listWebhookTriggers() }
  catch (e) { error.value = String(e.message || e) }
  finally { loading.value = false }
  if (config.gitEnabled) {
    try { providers.value = await api.gitProviders() } catch { /* selector stays empty */ }
  }
}
onMounted(load)

async function create() {
  if (!name.value.trim() || !promptTemplate.value.trim() || creating.value) return
  creating.value = true; error.value = ''
  try {
    fresh.value = await api.createWebhookTrigger({
      name: name.value.trim(),
      providerId: providerId.value || null,
      events: events.value,
      repoFilter: repoFilter.value.trim() || null,
      promptTemplate: promptTemplate.value,
      agent: agent.value,
      autoApprove: autoApprove.value
    })
    copiedUrl.value = false; copiedSecret.value = false
    name.value = ''; repoFilter.value = ''
    await load()
  } catch (e) { error.value = String(e.message || e) }
  finally { creating.value = false }
}

async function remove(t) {
  if (confirmingId.value !== t.id) { confirmingId.value = t.id; return }
  confirmingId.value = ''
  try {
    await api.deleteWebhookTrigger(t.id)
    if (fresh.value?.trigger?.id === t.id) fresh.value = null
    await load()
  } catch (e) { error.value = String(e.message || e) }
}

async function copyUrl() {
  try { await navigator.clipboard.writeText(fresh.value.trigger.url); copiedUrl.value = true }
  catch { /* clipboard unavailable — user can select manually */ }
}

async function copySecret() {
  try { await navigator.clipboard.writeText(fresh.value.secret); copiedSecret.value = true }
  catch { /* clipboard unavailable — user can select manually */ }
}

function providerName(id) {
  return providers.value.find(p => p.id === id)?.displayName || id
}
</script>

<template>
  <div class="embed">
    <div class="embed-inner">
      <h3 class="pane-head">Webhooks</h3>
      <p class="note">
        Start an autonomous session when a merge request / pull request event arrives from
        GitLab or GitHub. Paste the generated URL and secret into the repository's webhook
        settings (GitLab: Settings → Webhooks, "Merge request events" + secret token;
        GitHub: Settings → Webhooks, content type <code>application/json</code>,
        event "Pull requests" + secret).
      </p>

      <p v-if="error" class="err" data-webhook-error>{{ error }}</p>

      <div v-if="fresh" class="fresh" data-webhook-fresh>
        <p class="fresh-label">Webhook created — copy the URL and secret now, the secret will not be shown again:</p>
        <div class="fresh-row">
          <span class="fresh-key">URL</span>
          <code class="fresh-value" data-webhook-url>{{ fresh.trigger.url }}</code>
          <button @click="copyUrl">{{ copiedUrl ? 'Copied ✓' : 'Copy' }}</button>
        </div>
        <div class="fresh-row">
          <span class="fresh-key">Secret</span>
          <code class="fresh-value" data-webhook-secret>{{ fresh.secret }}</code>
          <button @click="copySecret">{{ copiedSecret ? 'Copied ✓' : 'Copy' }}</button>
        </div>
      </div>

      <section class="card">
        <div class="card-head">
          <h4>New webhook trigger</h4>
        </div>
        <div class="field">
          <label>Name</label>
          <input v-model="name" data-webhook-name placeholder="e.g. review new merge requests" @keyup.enter="create" />
        </div>
        <div class="field" v-if="providers.length">
          <label>Git account for cloning (optional — leave empty for public repositories)</label>
          <select v-model="providerId" data-webhook-provider>
            <option value="">none (public repository)</option>
            <option v-for="p in providers" :key="p.id" :value="p.id">{{ p.displayName }}</option>
          </select>
        </div>
        <div class="field">
          <label>Events</label>
          <div class="events">
            <label v-for="e in EVENT_OPTIONS" :key="e" class="check">
              <input type="checkbox" :value="e" v-model="events" :data-webhook-event="e" /> {{ e }}
            </label>
          </div>
        </div>
        <div class="field">
          <label>Repository filter (optional — substring of "group/repo"; other repositories are ignored)</label>
          <input v-model="repoFilter" data-webhook-repo-filter placeholder="e.g. my-group/" />
        </div>
        <div class="field">
          <label>Prompt template — placeholders: {&#123;title}}, {&#123;description}}, {&#123;source_branch}}, {&#123;target_branch}}, {&#123;url}}, {&#123;repo}}, {&#123;id}}, {&#123;action}}</label>
          <textarea v-model="promptTemplate" data-webhook-template rows="6"></textarea>
        </div>
        <div class="field row-2">
          <div>
            <label>Agent</label>
            <select v-model="agent" data-webhook-agent>
              <option>Claude</option>
              <option>Codex</option>
              <option>Cursor</option>
              <option>OpenClaw</option>
            </select>
          </div>
          <label class="check auto">
            <input type="checkbox" v-model="autoApprove" data-webhook-auto-approve />
            Auto-approve tool permissions (session runs unattended)
          </label>
        </div>
        <div class="actions">
          <button class="primary" data-webhook-create
            :disabled="creating || !name.trim() || !promptTemplate.trim() || !events.length"
            @click="create">{{ creating ? 'Creating…' : 'Create webhook' }}</button>
        </div>
      </section>

      <div class="list">
        <p v-if="loading" class="muted">Loading…</p>
        <p v-else-if="!triggers.length" class="muted" data-webhook-empty>No webhook triggers yet.</p>
        <div v-for="t in triggers" :key="t.id" class="trigger" data-webhook-row>
          <div class="trigger-main">
            <span class="trigger-name">{{ t.name }}</span>
            <code class="trigger-url">{{ t.url }}</code>
            <div class="trigger-meta">
              <span>{{ t.events.join(', ') }}</span>
              <span v-if="t.providerId">via {{ providerName(t.providerId) }}</span>
              <span v-if="t.repoFilter">filter: {{ t.repoFilter }}</span>
              <span>agent: {{ t.agent }}</span>
              <span v-if="t.lastTriggeredAt">last triggered {{ new Date(t.lastTriggeredAt).toLocaleString() }}</span>
            </div>
          </div>
          <button class="del" data-webhook-delete @click="remove(t)">
            {{ confirmingId === t.id ? 'Really delete?' : 'Delete' }}
          </button>
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.embed { flex: 1; overflow-y: auto; }
.embed-inner { max-width: 680px; padding: 26px 30px; }
.pane-head { font-size: 22px; margin: 0 0 16px; }
.note { color: var(--muted); font-size: 12px; line-height: 1.5; margin: 0 0 16px; }
.err { color: var(--danger); font-family: var(--mono); font-size: 12px; }
.muted { color: var(--muted); font-size: 13px; margin: 0; }
.card { background: var(--panel); border: 1px solid var(--border); border-radius: 12px; padding: 16px 18px; margin-bottom: 14px; }
.card-head h4 { margin: 0 0 10px; font-size: 15px; }
.field { margin-bottom: 12px; }
.field label { display: block; font-size: 12px; color: var(--muted-2); margin-bottom: 4px; }
.field input, .field select, .field textarea { width: 100%; }
.field textarea { font-family: var(--mono); font-size: 12px; resize: vertical; }
.events { display: flex; flex-wrap: wrap; gap: 12px; }
.check { display: flex; align-items: center; gap: 6px; font-size: 13px; cursor: pointer; }
.check input { width: auto; }
.row-2 { display: flex; gap: 18px; align-items: flex-end; }
.check.auto { margin-bottom: 6px; }
.actions { display: flex; justify-content: flex-end; }
.fresh { background: rgba(0,0,0,.25); border: 1px solid var(--accent); border-radius: 10px; padding: 12px; margin-bottom: 14px; }
.fresh-label { margin: 0 0 8px; font-size: 12px; color: var(--accent); }
.fresh-row { display: flex; align-items: center; gap: 10px; margin-bottom: 6px; }
.fresh-key { font-size: 11px; color: var(--muted-2); width: 44px; flex-shrink: 0; }
.fresh-value { font-family: var(--mono); font-size: 12px; word-break: break-all; flex: 1; }
.list { display: flex; flex-direction: column; gap: 8px; }
.trigger { display: flex; align-items: center; gap: 12px; border: 1px solid var(--border); border-radius: 10px; padding: 10px 12px; }
.trigger-main { min-width: 0; flex: 1; display: flex; flex-direction: column; gap: 2px; }
.trigger-name { font-weight: 600; }
.trigger-url { font-family: var(--mono); font-size: 11px; color: var(--muted); word-break: break-all; }
.trigger-meta { display: flex; flex-wrap: wrap; gap: 10px; font-size: 11px; color: var(--muted-2); }
</style>
