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
import SecretPicker from './SecretPicker.vue'
import SecretGrantsPicker from './SecretGrantsPicker.vue'
import { secretIdsChange, secretOptions, secretSelectionFor } from '../lib/secrets.js'
import AgentDecisionCard from './AgentDecisionCard.vue'
import AutoDeleteCard from './AutoDeleteCard.vue'
import SystemPromptField from './SystemPromptField.vue'
import SessionResourceFields from './SessionResourceFields.vue'
import { canApplyNow, resourceForm, resourcePayload, resourcesChanged } from '../lib/resources.js'

const props = defineProps({ session: Object, projects: Array, embedded: { type: Boolean, default: false } })
const emit = defineEmits(['close', 'updated'])

const f = ref({})
const resources = ref(resourceForm(null))
// A running interactive session gets new resources only through a restart; the owner chooses
// between now (pause and resume, conversation kept) and its next start.
const restartNow = ref(true)
const resourcesDirty = computed(() => resourcesChanged(resources.value, props.session))
const offerRestart = computed(() => !scheduled.value && canApplyNow(props.session) && (resourcesDirty.value || props.session.resourcesPendingRestart))
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
    schedule: session.schedule || '',
    description: session.description || '',
    systemPrompt: session.systemPrompt || '',
    image: session.image || '',
    runAsRoot: !!session.runAsRoot,
    autoApprove: !!session.autoApprove,
    autoUpdateRuntimeImages: session.autoUpdateRuntimeImages ?? 'default',
    mcpConfigJson: session.mcpConfigJson || '',
    projectId: session.projectId || '',
    ...defaultAgentForm(session),
    credentialId: session.credentialId || '',
    accountFailover: session.accountFailover === 'off' ? 'off' : 'auto'
  }
  autoDelete.value = autoDeleteForm(session)
  resources.value = resourceForm(session)
  restartNow.value = true
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

// Stored secrets, ticked as the session has them. The options follow the project chosen here, so
// moving the session shows what it would get in the other project (docs/session-secrets.md).
const storedSecrets = ref([])
const selectedProject = computed(() => (props.projects || []).find(project => project.id === f.value.projectId) || null)
const secrets = computed(() => secretOptions(storedSecrets.value, selectedProject.value))
const selectedGrants = ref([])
watch(() => props.session.id, () => { selectedGrants.value = (props.session.secretGrants || []).map(g => ({ ...g })) }, { immediate: true })
const selectedSecrets = ref([])
watch([secrets, () => props.session.id], ([options]) => {
  selectedSecrets.value = secretSelectionFor(props.session.secretIds, options, !!selectedProject.value)
}, { immediate: true })

onMounted(async () => {
  try { credentialStatus.value = await api.getCredentialStatus() } catch { /* advisory only */ }
  try { providerAccounts.value = (await api.listProviderAccounts()) || {} } catch { /* one login needs no choice */ }
  try {
    const allowed = await api.getAllowedAgents()
    allowedAgents.value = allowed?.agents || []
  } catch { /* keep unrestricted catalog */ }
  try { savedMcpServers.value = await api.mcpServers() } catch { /* library is optional */ }
  try { storedSecrets.value = (await api.listSecrets()) || [] } catch { /* older backend: no secrets */ }
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
      // safe to change for a scheduled session. Its schedule patches the CronJob itself;
      // the description ("" clears it) is plain metadata and applies immediately too.
      ? { schedule: f.value.schedule, title: f.value.title, description: f.value.description, projectId: f.value.projectId || null, autoApprove: f.value.autoApprove, ...expiry }
      : {
          ...expiry,
          title: f.value.title,
          description: f.value.description,  // "" clears it
          systemPrompt: f.value.systemPrompt, // "" clears it; a scheduled session never sends it
          policy: policyPayload(f.value),
          image: f.value.image.trim(),          // empty = default agent image
          runAsRoot: f.value.runAsRoot,
          autoApprove: f.value.autoApprove,
          ...resourcePayload(resources.value),
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
      if (JSON.stringify(selectedGrants.value) !== JSON.stringify(props.session.secretGrants || [])) payload.secretGrants = selectedGrants.value
      Object.assign(payload, credentialChange())
      Object.assign(payload, gitPatIdsChange(selectedGitPats.value, gitPats.value, props.session.gitPatIds))
      Object.assign(payload, secretIdsChange(selectedSecrets.value, secrets.value, !!selectedProject.value, props.session.secretIds))
      // Sent only when changed: null means "unchanged" on the API, and the hub reads the stored
      // value when a limit is reported, so this applies to the running session at once.
      const storedFailover = props.session.accountFailover === 'off' ? 'off' : 'auto'
      if (f.value.accountFailover !== storedFailover) payload.accountFailover = f.value.accountFailover
    }
    if (f.value.autoUpdateRuntimeImages !== (props.session.autoUpdateRuntimeImages ?? 'default'))
      payload.autoUpdateRuntimeImages = f.value.autoUpdateRuntimeImages === 'default' ? null : f.value.autoUpdateRuntimeImages
    const restart = offerRestart.value && restartNow.value
    let updated = await api.updateSession(props.session.id, payload)
    if (restart) {
      // Saved first, so a refused restart loses nothing: the values then apply at the next start.
      try { updated = await api.applySessionResources(props.session.id) }
      catch (e) {
        error.value = `Saved, but the session was not restarted: ${e.message || e} The new resources apply the next time it starts.`
        return
      }
    }
    emit('updated', updated)
  } catch (e) { error.value = String(e.message || e) }
  finally { busy.value = false }
}
</script>

<template>
  <div :class="embedded ? 'embed' : 'overlay'" @click.self="embedded || $emit('close')">
    <div :class="embedded ? 'embed-inner' : 'modal'">
      <h3 class="form-title">Edit session</h3>
      <p class="note" v-if="scheduled">Schedule changes apply to future runs and leave an active Job running. Delete and recreate the session to change its agent, billing, policy, or runtime settings.</p>
      <p class="note" v-else>The title, auto approve and auto-delete apply immediately. Image, root mode, resources and the system prompt take effect the next time the session is resumed; new resources can also restart a running session at once.</p>

      <div v-if="scheduled" class="card sect">
        <div class="field">
          <label>Schedule <span class="dim">— cron, UTC</span></label>
          <input v-model="f.schedule" data-schedule maxlength="128" placeholder="0 6 * * 1-5" />
        </div>
        <p class="note" data-next-run>Next run (UTC): {{ session.nextRunAt ? new Date(session.nextRunAt).toISOString().replace('T', ' ').replace('.000Z', '') : 'No upcoming run' }}. Saved schedule; updates after saving. Runs may wait while a previous Job is active.</p>
      </div>
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
        <SecretPicker v-if="!scheduled" v-model="selectedSecrets" :options="secrets" :in-project="!!selectedProject"
          :project-has-none="!!selectedProject && storedSecrets.length > 0" />
        <SecretGrantsPicker v-if="!scheduled" v-model="selectedGrants" :options="storedSecrets" :effective="session.effectiveSecrets || []" />
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
      <label>Automatic runtime image updates
        <select v-model="f.autoUpdateRuntimeImages">
          <option value="default">Deployment default</option>
          <option :value="true">On when safe</option>
          <option :value="false">Off</option>
        </select>
      </label>
      <AutoDeleteCard v-model="autoDelete" :scheduled="scheduled" :expires-at="session.expiresAt || null" />
      <template v-if="!scheduled">
        <AgentDecisionCard v-model:agent="f.agent" v-model:auth-mode="f.authMode"
          v-model:open-claw-api-key-source="f.openClawApiKeySource" v-model:credential-id="f.credentialId"
          v-model:model-endpoint-url="f.modelEndpointUrl" v-model:model-name="f.modelName"
          :accounts="providerAccounts" :mode="session.mode"
          :legacy-auth-mode="session.authMode" :credential-status="credentialStatus" :options="agentChoices" />
        <div v-if="!['ApiKey', 'CustomEndpoint'].includes(f.authMode)" class="card sect toggle-field failover-field">
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
            <div class="field"><label>Container image</label><input v-model="f.image" class="mono" placeholder="default agent image" /></div>
            <SessionResourceFields v-model="resources" :session="session" />
            <label class="check">
              <input type="checkbox" v-model="f.runAsRoot" />
              <span><b>Run as root</b> — install tools via apt, npm&nbsp;-g, …</span>
            </label>
          </div>
        </div>
      </template>

      <div v-if="offerRestart" class="card sect restart-note" data-resources-restart>
        <label class="check">
          <input type="checkbox" v-model="restartNow" data-resources-restart-now />
          <span><b>Restart now to apply the new resources.</b> The session is paused and resumed: its state is saved,
            the pod is replaced and the conversation continues. A turn in progress is interrupted, and the terminal
            reconnects after the restart.</span>
        </label>
        <p v-if="!restartNow" class="note restart-later">The new resources apply the next time the session is paused and resumed.</p>
      </div>
      <p v-else-if="resourcesDirty && !scheduled && session.phase === 'Running'" class="note" data-resources-next-start>
        New resources apply at the session's next start; a running {{ session.mode.toLowerCase() }} session is not restarted for them.
      </p>
      <p v-if="error" class="err">{{ error }}</p>
      <div class="row">
        <button class="primary" data-submit :disabled="busy" @click="save">{{ busy ? (offerRestart && restartNow ? 'Saving and restarting…' : 'Saving…') : (offerRestart && restartNow ? 'Save and restart' : 'Save changes') }}</button>
        <button @click="$emit('close')">Cancel</button>
        <span v-if="!(offerRestart && restartNow)" class="dim note-inline">Changes apply on next agent turn — the session keeps running.</span>
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
.restart-note { border-color: var(--warn); }
.restart-note .check { margin: 0; }
.restart-later { margin: 10px 0 0; }
@media (max-width: 760px) {
  .grid3 { grid-template-columns: 1fr; }
  .row { align-items: stretch; flex-wrap: wrap; }
  .note-inline { flex-basis: 100%; }
}
</style>
