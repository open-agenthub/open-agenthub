<script setup>
import { computed, onMounted, ref, watch } from 'vue'
import { api } from '../api.js'
import {
  accountsFor, agentPayload, buildEphemeralApiSources, defaultAccountId, defaultAgentForm, ephemeralNameFromUrl,
  filterAgentOptions, gitPatIdsChange, gitPatOptions, gitPatSelectionFor, mcpBadgeLabel, policyPayload,
  toolsPlaceholder, commandsPlaceholder
} from '../lib/agent.js'
import { autoDeleteForm, autoDeletePayload } from '../lib/expiry.js'
import RepoPicker from './RepoPicker.vue'
import GitPatPicker from './GitPatPicker.vue'
import AgentDecisionCard from './AgentDecisionCard.vue'
import AutoDeleteCard from './AutoDeleteCard.vue'
import SystemPromptField from './SystemPromptField.vue'

const props = defineProps({ session: Object, projects: Array, embedded: { type: Boolean, default: false } })
const emit = defineEmits(['close', 'updated'])

const f = ref({})
const autoDelete = ref(autoDeleteForm(null))
const repos = ref([])
const advOpen = ref(false)
const busy = ref(false)
const error = ref('')
const credentialStatus = ref({})
const providerAccounts = ref({})
// Stored git PATs, ticked as the session has them (null = all). Re-derived when the list
// arrives or the session changes; the change is only sent when it differs (see gitPatIdsChange).
const gitPats = computed(() => gitPatOptions(credentialStatus.value))
const selectedGitPats = ref([])
watch([gitPats, () => props.session.id], ([options]) => {
  selectedGitPats.value = gitPatSelectionFor(props.session.gitPatIds, options)
})
const allowedAgents = ref([])
const agentChoices = computed(() => filterAgentOptions(allowedAgents.value, { include: props.session?.agent }))
// Saved MCP servers from the personal library (own + org + shared with me).
const savedMcpServers = ref([])
const selectedMcpIds = ref([])
const ephemeralUrl = ref('')
const ephemeralName = ref('')
const ephemeralSaveToLibrary = ref(false)

const scheduled = computed(() => props.session.mode === 'Scheduled')
const automated = computed(() => props.session.mode !== 'Interactive')

function reset(session) {
  f.value = {
    title: session.title,
    description: session.description || '',
    systemPrompt: session.systemPrompt || '',
    image: session.image || '',
    runAsRoot: !!session.runAsRoot,
    autoApprove: !!session.autoApprove,
    cpu: session.cpu || '500m',
    memory: session.memory || '1Gi',
    mcpConfigJson: session.mcpConfigJson || '',
    projectId: session.projectId || '',
    ...defaultAgentForm(session),
    credentialId: session.credentialId || '',
    accountFailover: session.accountFailover === 'off' ? 'off' : 'auto'
  }
  autoDelete.value = autoDeleteForm(session)
  repos.value = (session.repos || []).map(repo => ({ ...repo }))
  selectedMcpIds.value = [...(session.mcpServerIds || [])]
  ephemeralUrl.value = ''
  ephemeralName.value = ''
  ephemeralSaveToLibrary.value = false
  busy.value = false
  error.value = ''
}

reset(props.session)
watch(() => props.session.id, () => reset(props.session))

onMounted(async () => {
  try { credentialStatus.value = await api.getCredentialStatus() } catch { /* advisory only */ }
  try { providerAccounts.value = (await api.listProviderAccounts()) || {} } catch { /* one login needs no choice */ }
  try {
    const allowed = await api.getAllowedAgents()
    allowedAgents.value = allowed?.agents || []
  } catch { /* keep unrestricted catalog */ }
  try { savedMcpServers.value = await api.mcpServers() } catch { /* library is optional */ }
})

watch(ephemeralUrl, (url) => {
  ephemeralName.value = String(url || '').trim() ? ephemeralNameFromUrl(url) : ''
})

/**
 * The account is sent only when the person changed it. A session without a pin shows the
 * default preselected; sending that back would pin the session to today's default and detach
 * it from a default changed later, which is not what leaving a field alone should do.
 */
function credentialChange() {
  const chosen = f.value.credentialId || ''
  const current = props.session.credentialId || ''
  if (chosen === current) return {}
  if (!current && chosen === defaultAccountId(accountsFor(providerAccounts.value, f.value.agent))) return {}
  return { credentialId: chosen }
}

async function save() {
  busy.value = true; error.value = ''
  if (f.value.mcpConfigJson.trim()) {
    try { JSON.parse(f.value.mcpConfigJson) }
    catch { error.value = 'MCP config is not valid JSON.'; busy.value = false; return }
  }
  try {
    const ephemeralApiSources = buildEphemeralApiSources({
      url: ephemeralUrl.value,
      name: ephemeralName.value,
      saveToLibrary: ephemeralSaveToLibrary.value
    })
    // Off has to be said as 0: null would mean "leave the stored deadline alone".
    const expiry = autoDeletePayload(autoDelete.value, { off: 0 })
    const payload = scheduled.value
      // Auto approve and the auto-delete deadline are not part of the pod spec, so they are
      // safe to change even for a scheduled session (everything else there is fixed by the
      // CronJob spec). The description ("" clears it) is plain metadata and applies immediately too.
      ? { title: f.value.title, description: f.value.description, projectId: f.value.projectId || null, autoApprove: f.value.autoApprove, ...expiry }
      : {
          ...expiry,
          title: f.value.title,
          description: f.value.description,  // "" clears it
          systemPrompt: f.value.systemPrompt, // "" clears it; a scheduled session never sends it
          policy: policyPayload(f.value),
          image: f.value.image.trim(),          // empty = default agent image
          runAsRoot: f.value.runAsRoot,
          autoApprove: f.value.autoApprove,
          cpu: f.value.cpu.trim(),
          memory: f.value.memory.trim(),
          repos: repos.value,
          mcpConfigJson: f.value.mcpConfigJson,  // "" clears it
          mcpServerIds: selectedMcpIds.value,    // [] = none, null would mean unchanged
          // Omit when blank so existing session ephemerals are left unchanged (null on API).
          ...(ephemeralApiSources.length ? { ephemeralApiSources } : {}),
          projectId: f.value.projectId || null
        }
    if (!scheduled.value && f.value.authMode !== 'Auto') {
      Object.assign(payload, agentPayload(f.value))
    }
    if (!scheduled.value) {
      Object.assign(payload, credentialChange())
      Object.assign(payload, gitPatIdsChange(selectedGitPats.value, gitPats.value, props.session.gitPatIds))
      // Sent only when changed: null means "unchanged" on the API, and the hub reads the stored
      // value when a limit is reported, so this applies to the running session at once.
      const storedFailover = props.session.accountFailover === 'off' ? 'off' : 'auto'
      if (f.value.accountFailover !== storedFailover) payload.accountFailover = f.value.accountFailover
    }
    const updated = await api.updateSession(props.session.id, payload)
    emit('updated', updated)
  } catch (e) { error.value = String(e.message || e) }
  finally { busy.value = false }
}
</script>

<template>
  <div :class="embedded ? 'embed' : 'overlay'" @click.self="embedded || $emit('close')">
    <div :class="embedded ? 'embed-inner' : 'modal'">
      <h3 class="form-title">Edit session</h3>
      <p class="note" v-if="scheduled">Scheduled sessions run from a fixed CronJob spec — delete and recreate the session to change its agent, billing, policy, or runtime settings.</p>
      <p class="note" v-else>The title, auto approve and auto-delete apply immediately. Image, root mode, resources and the system prompt take effect the next time the session is resumed.</p>

      <div class="card sect">
        <div class="field">
          <label>Title</label>
          <input v-model="f.title" />
        </div>
        <div class="field">
          <label>Description <span class="dim">— what is this agent for? Shown to the other agents of the project.</span></label>
          <input v-model="f.description" data-description maxlength="500" placeholder="e.g. Reviews merge requests and hands findings to the coder" />
        </div>
        <SystemPromptField v-model="f.systemPrompt" :readonly="scheduled"
          readonly-hint="Fixed by the CronJob spec — delete and recreate the session to change it." />
        <div class="field last"><label>Project</label><select v-model="f.projectId"><option value="">No project</option><option v-for="project in projects" :key="project.id" :value="project.id">{{ project.name }}</option></select></div>
        <div class="field last toggle-field">
          <label>Auto approve <span class="dim">— run tools without asking</span></label>
          <button type="button" class="toggle" role="switch" data-auto-approve
                  :aria-checked="f.autoApprove ? 'true' : 'false'" :class="{ on: f.autoApprove }"
                  @click="f.autoApprove = !f.autoApprove">
            <span class="knob"></span>
            <span class="toggle-label">{{ f.autoApprove ? 'On' : 'Off' }}</span>
          </button>
          <p v-if="f.autoApprove && f.runAsRoot" class="warn">
            With <b>Run as root</b> the agent may run any command as root in this container,
            unattended. Only do this for a container you would hand over anyway.
          </p>
        </div>
      </div>
      <AutoDeleteCard v-model="autoDelete" :scheduled="scheduled" :expires-at="session.expiresAt || null" />
      <template v-if="!scheduled">
        <AgentDecisionCard v-model:agent="f.agent" v-model:auth-mode="f.authMode"
          v-model:open-claw-api-key-source="f.openClawApiKeySource" v-model:credential-id="f.credentialId"
          :accounts="providerAccounts" :mode="session.mode"
          :legacy-auth-mode="session.authMode" :credential-status="credentialStatus" :options="agentChoices" />
        <div v-if="f.authMode !== 'ApiKey'" class="card sect toggle-field failover-field">
          <label>Switch account automatically at usage limit <span class="dim">— move to another stored login when this one is used up</span></label>
          <button type="button" class="toggle" role="switch" data-account-failover
                  :aria-checked="f.accountFailover !== 'off' ? 'true' : 'false'" :class="{ on: f.accountFailover !== 'off' }"
                  @click="f.accountFailover = f.accountFailover === 'off' ? 'auto' : 'off'">
            <span class="knob"></span>
            <span class="toggle-label">{{ f.accountFailover !== 'off' ? 'On' : 'Off' }}</span>
          </button>
          <p class="note failover-note">Applies at once. Off keeps the session on its account when that hits a limit; the hub still marks the account so new sessions avoid it.</p>
        </div>
        <div class="card sect">
          <label>Repositories</label>
          <RepoPicker v-model="repos" />
          <GitPatPicker v-model="selectedGitPats" :options="gitPats" />
        </div>
        <div class="card adv">
          <button type="button" class="adv-head" data-advanced :aria-expanded="advOpen" @click="advOpen = !advOpen">
            <span><b>Advanced</b><span class="dim adv-sub">policy, MCP, container, resources</span></span>
            <span class="dim">{{ advOpen ? '▾' : '▸' }}</span>
          </button>
          <div v-if="advOpen" class="adv-body">
            <div v-if="automated" class="policy-block">
              <p class="policy-note">Automation is default-deny. Add one entry per line; empty fields allow nothing.</p>
              <p v-if="f.agent === 'Claude'" class="policy-note" data-claude-command-semantics>
                Claude shell entries become exact native Bash rules; metacharacters, globs, and compound commands are rejected. Deliberate native Bash(...) patterns belong under built-in tools.
              </p>
              <div class="field">
                <label>Built-in tools and patterns</label>
                <textarea v-model="f.allowedToolsRaw" data-policy="allowedTools" :placeholder="toolsPlaceholder(f.agent)" />
              </div>
              <div class="field">
                <label>Full MCP tool names and patterns</label>
                <textarea v-model="f.allowedMcpToolsRaw" data-policy="allowedMcpTools" placeholder="mcp__docs__search\nmcp__git__*" />
              </div>
              <div class="field" v-if="f.agent !== 'Cursor'">
                <label>{{ f.agent === 'Claude' ? 'Exact shell commands' : 'Shell command prefixes' }}</label>
                <textarea v-model="f.allowedCommandsRaw" data-policy="allowedCommands" :placeholder="commandsPlaceholder(f.agent)" />
              </div>
            </div>
            <div v-if="savedMcpServers.length" class="field" data-mcp-picker>
              <label>Saved MCP servers <span class="dim">— from your library</span></label>
              <label v-for="s in savedMcpServers" :key="s.id" class="check mcp-pick">
                <input type="checkbox" :value="s.id" v-model="selectedMcpIds" data-mcp-option />
                <span>{{ s.name }} <span class="mcp-badge" :data-mcp-badge="s.id">{{ mcpBadgeLabel(s) }}</span></span>
              </label>
            </div>
            <div class="field" data-ephemeral-api>
              <label>API URL <span class="dim">— OpenAPI/GraphQL for this session</span></label>
              <input v-model="ephemeralUrl" data-ephemeral-url class="mono" placeholder="https://…/openapi.json" />
              <div class="ephemeral-row">
                <input v-model="ephemeralName" data-ephemeral-name placeholder="name" />
                <label class="check ephemeral-save">
                  <input type="checkbox" v-model="ephemeralSaveToLibrary" data-ephemeral-save />
                  <span>Save to my library</span>
                </label>
              </div>
            </div>
            <div class="field">
              <label>Extra tools <span class="dim">— MCP servers (.mcp.json), empty = none</span></label>
              <textarea v-model="f.mcpConfigJson" placeholder='{ "mcpServers": { … } }'></textarea>
            </div>
            <div class="grid3">
              <div class="field"><label>Container image</label><input v-model="f.image" class="mono" placeholder="default agent image" /></div>
              <div class="field"><label>CPU</label><input v-model="f.cpu" class="mono" placeholder="500m" /></div>
              <div class="field"><label>Memory</label><input v-model="f.memory" class="mono" placeholder="1Gi" /></div>
            </div>
            <label class="check">
              <input type="checkbox" v-model="f.runAsRoot" />
              <span><b>Run as root</b> — install tools via apt, npm&nbsp;-g, …</span>
            </label>
          </div>
        </div>
      </template>

      <p v-if="error" class="err">{{ error }}</p>
      <div class="row">
        <button class="primary" data-submit :disabled="busy" @click="save">{{ busy ? 'Saving…' : 'Save changes' }}</button>
        <button @click="$emit('close')">Cancel</button>
        <span class="dim note-inline">Changes apply on next agent turn — the session keeps running.</span>
      </div>
    </div>
  </div>
</template>

<style scoped>
.overlay { position: fixed; inset: 0; background: rgba(10,9,8,.7); display: flex; align-items: flex-start; justify-content: center; padding: 24px; overflow-y: auto; z-index: 50; }
.modal { width: 620px; max-width: 100%; background: var(--panel); border: 1px solid var(--border-2); border-radius: var(--radius-lg); padding: 22px; }
.embed-inner { max-width: 720px; }
.form-title { font-size: 20px; margin: 0 0 6px; }
.note { color: var(--muted-2); font-size: 12px; line-height: 1.5; margin: 0 0 16px; }
.sect { padding: 18px 20px; margin-bottom: 16px; }
.field.last { margin-bottom: 0; }
.dim { color: var(--faint); font-weight: 400; }
.mono { font-family: var(--mono); font-size: 13px; }
.adv { overflow: hidden; margin-bottom: 16px; }
.adv-head { width: 100%; display: flex; align-items: center; justify-content: space-between; padding: 14px 20px; border: none; background: none; border-radius: 0; font-size: 13px; color: var(--text); }
.adv-head:hover { background: var(--hover); }
.adv-sub { margin-left: 10px; font-size: 12px; }
.adv-body { padding: 14px 20px 20px; border-top: 1px solid var(--border); }
.policy-block { margin-bottom: 18px; padding-bottom: 4px; border-bottom: 1px solid var(--border); }
.policy-note { margin: 0 0 12px; color: var(--muted-2); font-size: 12px; line-height: 1.5; }
.grid3 { display: grid; grid-template-columns: 2fr 1fr 1fr; gap: 14px; }
.check { display: flex; align-items: flex-start; gap: 10px; margin: 4px 0 0; font-size: 13px; color: var(--text); cursor: pointer; }
.check input { width: auto; margin-top: 2px; }
.mcp-pick { margin: 0 0 6px; }
.mcp-badge { display: inline-block; margin-left: 6px; padding: 1px 6px; border: 1px solid var(--border-2); border-radius: 4px; color: var(--muted-3); font-size: 11px; font-weight: 700; }
.ephemeral-row { display: flex; flex-wrap: wrap; align-items: center; gap: 12px; margin-top: 8px; }
.ephemeral-row input[data-ephemeral-name] { width: 180px; max-width: 100%; }
.ephemeral-save { margin: 0; align-items: center; }
.ephemeral-save input { margin-top: 0; }
.row { display: flex; align-items: center; gap: 10px; padding-bottom: 8px; }
.note-inline { font-size: 12px; }
.err { color: var(--danger); font-family: var(--mono); font-size: 12px; }
.warn { margin: 10px 0 0; font-size: 12px; line-height: 1.5; color: var(--warn); }
.toggle-field { margin-top: 16px; padding-top: 16px; border-top: 1px solid var(--border); }
.failover-field { margin-top: 0; border-top: none; }
.failover-note { margin: 10px 0 0; }
.toggle { display: inline-flex; align-items: center; gap: 10px; width: auto; padding: 5px 14px 5px 6px; border: 1px solid var(--border-2); border-radius: 999px; background: none; font-size: 12px; color: var(--muted-3); }
.toggle:hover { background: var(--hover); }
.toggle .knob { width: 30px; height: 17px; padding: 2px; border-radius: 999px; background: var(--border-3); transition: background .15s; }
.toggle .knob::after { content: ''; display: block; width: 13px; height: 13px; border-radius: 50%; background: var(--panel); transition: transform .15s; }
.toggle.on { border-color: var(--warn); color: var(--warn); }
.toggle.on .knob { background: var(--warn); }
.toggle.on .knob::after { transform: translateX(13px); }
.toggle-label { font-weight: 700; }
@media (max-width: 760px) {
  .grid3 { grid-template-columns: 1fr; }
  .row { align-items: stretch; flex-wrap: wrap; }
  .note-inline { flex-basis: 100%; }
}
</style>
