<script setup>
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { api } from '../../api.js'
// trigger: false hides the own button; the session bar's "⋯" menu opens the panel instead (#551).
// The component stays mounted either way: it is what polls a running replacement's status.
const props = defineProps({ session: { type: Object, required: true }, client: { type: Object, default: () => api }, trigger: { type: Boolean, default: true } })
const emit = defineEmits(['updated', 'pending'])
const reviewElement = ref(null)
const open = ref(false), status = ref(null), error = ref(''), busy = ref(false), review = ref(null)
const accounts = ref({}), catalog = ref(null), auth = ref('Subscription'), credential = ref(''), endpoint = ref(''), model = ref('')
const instructions = ref(''), task = ref(''), decisions = ref(''), pendingWork = ref(''), references = ref('')
function newRequestId() {
  const bytes = crypto.getRandomValues(new Uint8Array(16))
  bytes[6] = (bytes[6] & 15) | 64
  bytes[8] = (bytes[8] & 63) | 128
  const hex = Array.from(bytes, value => value.toString(16).padStart(2, '0')).join('')
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`
}
const requestId = ref(newRequestId())
const target = computed(() => props.session.agent === 'Claude' ? 'Codex' : 'Claude')
const operation = computed(() => status.value?.operation)
const active = computed(() => operation.value && !['completed', 'cancelled'].includes(operation.value.progress.phase))
const choices = computed(() => accounts.value[target.value] || [])
const models = computed(() => catalog.value?.catalog?.models || [])
const phaseLabel = computed(() => ({ requested: 'Waiting for a safe point', prepared: 'Source saved',
  cancelling: 'Confirming cancellation', stopping: 'Stopping the source', 'source-stopped': 'Starting the new provider', 'target-admitted': 'Starting the new provider',
  'target-starting': 'Starting the new provider', 'target-started': 'Confirming the handover', committing: 'Finishing the replacement', releasing: 'Activating the new provider' })[operation.value?.progress.phase] || 'Replacement pending')
const reviewedAccount = computed(() => choices.value.find(account => account.id === review.value?.target.credentialId)?.label
  || review.value?.target.credentialId || (review.value?.target.authMode === 'ApiKey' ? 'Stored API key' : 'Custom endpoint'))
const canCancel = computed(() => active.value && ['requested', 'prepared'].includes(operation.value.progress.phase))
let disposed = false, timer, catalogGeneration = 0
async function load() {
  try {
    const value = await props.client.providerSwitchStatus(props.session.id)
    if (disposed) return
    const previousPhase = status.value?.operation?.progress.phase
    status.value = value
    if (value.operation?.progress.phase !== previousPhase && ['completed', 'cancelled'].includes(value.operation?.progress.phase)) emit('updated')
    emit('pending', !!(value.operation && !['completed', 'cancelled'].includes(value.operation.progress.phase)))
  } catch (failure) { if (!disposed) error.value = failure.message || 'Could not read provider replacement status.' }
}
async function loadCatalog() {
  const generation = ++catalogGeneration
  catalog.value = null
  if (auth.value === 'CustomEndpoint' && !endpoint.value.trim()) return
  try {
    const value = await props.client.modelCatalog({ agent: target.value, authMode: auth.value,
      ...(credential.value ? { credentialId: credential.value } : {}),
      ...(auth.value === 'CustomEndpoint' ? { endpointUrl: endpoint.value.trim() } : {}) })
    if (!disposed && generation === catalogGeneration) catalog.value = value
  } catch { /* Admission reports missing capacity; a native default still has its conservative floor. */ }
}
function selection() {
  return { requestId: requestId.value,
    target: { agent: target.value, authMode: auth.value, credentialId: auth.value === 'Subscription' ? credential.value || null : null,
      modelEndpointUrl: auth.value === 'CustomEndpoint' ? endpoint.value.trim() : null, modelName: model.value || null },
    handover: { target: target.value, instructions: instructions.value, task: task.value, decisions: decisions.value,
      pendingWork: pendingWork.value, references: references.value.split('\n').map(value => value.trim()).filter(Boolean) } }
}
async function preview() {
  busy.value = true; error.value = ''; review.value = null
  try {
    review.value = await props.client.previewProviderSwitch(props.session.id, selection())
    await nextTick()
    reviewElement.value?.scrollIntoView?.({ block: 'start' })
    reviewElement.value?.focus({ preventScroll: true })
  }
  catch (failure) { error.value = failure.message || 'Could not review this replacement.' }
  finally { busy.value = false }
}
async function start() {
  if (!review.value || busy.value) return
  busy.value = true; error.value = ''
  try {
    const value = await props.client.startProviderSwitch(props.session.id, { ...selection(), reviewHash: review.value.reviewHash })
    status.value = { ...status.value, operation: value }; emit('pending', true); emit('updated'); await load()
  } catch (failure) { error.value = failure.message || 'Replacement admission failed.' }
  finally { busy.value = false }
}
async function act(action) {
  if (!operation.value || busy.value) return
  busy.value = true; error.value = ''
  try {
    status.value = await props.client[action](props.session.id, operation.value.request.requestId)
    if (action === 'cancelProviderSwitch') { requestId.value = newRequestId(); review.value = null }
    emit('updated'); await load()
  } catch (failure) { error.value = failure.message || 'Could not update the replacement.' }
  finally { busy.value = false }
}
async function toggle() {
  open.value = !open.value
  if (!open.value) return
  await load()
  try { accounts.value = await props.client.listProviderAccounts() } catch { accounts.value = {} }
  await loadCatalog()
}
watch(target, () => { credential.value = '' })
watch([auth, credential, endpoint, target], () => { model.value = ''; void loadCatalog() })
watch(() => JSON.stringify(selection()), () => { review.value = null })
watch(() => props.session.executionGeneration, () => { review.value = null; requestId.value = newRequestId(); void load() })
onMounted(() => { void load(); timer = setInterval(() => { if (open.value || active.value) void load() }, 2000) })
onBeforeUnmount(() => { disposed = true; catalogGeneration++; clearInterval(timer) })
defineExpose({ toggle, active })
</script>

<template>
  <div class="provider-switch" :class="{ bare: !trigger }" data-provider-switch>
    <button v-if="trigger" type="button" class="bar-btn" :aria-expanded="open" @click="toggle">{{ active ? 'Provider replacement' : 'Switch provider' }}</button>
    <section v-if="open" class="provider-panel" role="dialog" aria-label="Switch provider" data-provider-panel @keydown.esc="open = false">
      <p v-if="error" role="alert">{{ error }}</p>
      <template v-if="active">
        <p role="status">Switching to {{ operation.request.target.agent }}: {{ phaseLabel }}.</p>
        <p>The workspace and visible conversation stay with this session. Input waits until the reviewed handover is confirmed.</p>
        <p v-if="operation.progress.error" role="alert">{{ operation.progress.error }}</p>
        <button v-if="operation.progress.error" type="button" class="bar-btn" :disabled="busy" @click="act('retryProviderSwitch')">Retry this replacement</button>
        <button v-if="canCancel" type="button" class="bar-btn" :disabled="busy" @click="act('cancelProviderSwitch')">Cancel before source stop</button>
        <p v-else-if="operation.progress.phase === 'cancelling'">Cancellation is being confirmed. Input waits until it completes.</p>
        <p v-else>Source stop has begun. Automatic rollback and handover replay are unavailable.</p>
      </template>
      <template v-else-if="status?.available">
        <h3>Continue with {{ target }}</h3>
        <p>Your repositories and files stay on this session's volume. {{ target }} starts fresh native context with only the text you review below. Earlier messages remain visible.</p>
        <form @submit.prevent="preview">
          <label>Target sign-in<select v-model="auth" :disabled="busy"><option>Subscription</option><option value="ApiKey">API key</option><option value="CustomEndpoint">Custom endpoint</option></select></label>
          <label v-if="auth === 'Subscription'">Target account<select v-model="credential" :disabled="busy"><option value="">Current available default</option><option v-for="account in choices" :key="account.id" :value="account.id">{{ account.label || account.id }}</option></select></label>
          <label v-if="auth === 'CustomEndpoint'">Model endpoint<input v-model="endpoint" type="url" placeholder="https://model.example.com/v1" :disabled="busy" required></label>
          <label>Target model<select v-model="model" :disabled="busy" :required="auth === 'CustomEndpoint'"><option v-if="auth !== 'CustomEndpoint'" value="">Native default</option><option v-for="item in models" :key="item.id" :value="item.id">{{ item.label || item.id }}</option></select></label>
          <p v-if="auth === 'CustomEndpoint'">The target endpoint's model catalog must already report enough context capacity. Discover it in an existing session first.</p>
          <label>Instructions to preserve<textarea v-model="instructions" required :disabled="busy" maxlength="16000" /></label>
          <label>Task to continue<textarea v-model="task" required :disabled="busy" maxlength="16000" /></label>
          <label>Decisions<textarea v-model="decisions" :disabled="busy" maxlength="16000" /></label>
          <label>Pending work and questions<textarea v-model="pendingWork" :disabled="busy" maxlength="16000" /></label>
          <label>References, one per line<textarea v-model="references" :disabled="busy" maxlength="16000" /></label>
          <button type="submit" class="bar-btn" :disabled="busy">Review exact handover</button>
        </form>
        <div v-if="review" data-provider-review>
          <p>{{ review.policy }}</p>
          <p>Target account: {{ reviewedAccount }}. Context admission: {{ review.capacity.windowTokens.toLocaleString() }} tokens ({{ review.capacity.source === 'native-minimum' ? 'conservative native minimum' : 'reported model capacity' }}).</p>
          <pre ref="reviewElement" tabindex="0">{{ review.text }}</pre>
          <p>Switch only after the current turn has finished. Draft input, menus and approvals must be cleared first. Selected text is never silently shortened.</p>
          <button type="button" class="bar-btn primary" :disabled="busy" @click="start">Switch to {{ target }} with this handover</button>
        </div>
      </template>
      <p v-else>{{ status?.reason || 'Checking switching availability…' }}</p>
      <details v-if="status?.completedUsage?.length">
        <summary>Completed provider usage</summary>
        <p>Reported totals for completed provider runs; the current run continues in the session's usage totals.</p>
        <div class="usage-table"><table><thead><tr><th>Run</th><th>Provider</th><th>Account / billing</th><th>Input tokens</th><th>Output tokens</th></tr></thead>
          <tbody><tr v-for="item in status.completedUsage" :key="item.generation"><td>{{ item.generation + 1 }}</td><td>{{ item.agent }}</td>
            <td>{{ (accounts[item.agent] || []).find(account => account.id === item.credentialId)?.label || item.credentialId || item.billing }}</td>
            <td>{{ item.inputTokens.toLocaleString() }}</td><td>{{ item.outputTokens.toLocaleString() }}</td></tr></tbody></table></div>
      </details>
      <button type="button" class="bar-btn" @click="open = false">Close</button>
    </section>
  </div>
</template>

<style scoped>
/* Without its button the wrapper must not take a slot in the bar's flex row. */
.bare { display: contents; }
.provider-panel { box-sizing: border-box; position: absolute; z-index: 30; inset: 100% 12px auto auto; width: min(600px, calc(100vw - 32px)); max-height: 75vh; overflow: auto; padding: 18px; border: 1px solid var(--border); border-radius: 10px; background: var(--panel); box-shadow: 0 8px 32px #0008; }
label { display: grid; gap: 5px; margin: 12px 0; }
input, select, textarea { box-sizing: border-box; width: 100%; color: var(--text); background: var(--bg); border: 1px solid var(--border); padding: 8px; border-radius: 5px; }
textarea { min-height: 70px; resize: vertical; }
pre { white-space: pre-wrap; overflow-wrap: anywhere; border: 1px solid var(--border); padding: 12px; font: inherit; }
p { overflow-wrap: anywhere; }
.usage-table { max-width: 100%; overflow-x: auto; }
th, td { padding: 8px; text-align: left; }
</style>
