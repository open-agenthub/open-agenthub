<script setup>
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import TerminalPane from './TerminalPane.vue'
import SessionWorkspace from './SessionWorkspace.vue'
import ChatPane from './ChatPane.vue'
import ShareSessionDialog from './ShareSessionDialog.vue'
import ConvertSessionCard from './ConvertSessionCard.vue'
import { canPause, sessionStatus, statusStyle, tabLabel } from '../lib/status.js'
import { sessionCapabilities } from '../lib/access.js'
import { canConvert, convertedLabel } from '../lib/conversion.js'
import { api, getSharedConversation } from '../api.js'
import { repoShortName } from '../lib/text.js'
import { accountOptionLabel, accountsFor, authLabel, defaultAccountId } from '../lib/agent.js'
import { conversationState, mergeConversationPage, toTranscriptItems } from '../lib/transcript.js'
import { permissionTitle } from '../lib/permissions.js'
import { formatRemaining, isExpiringSoon } from '../lib/expiry.js'

const props = defineProps({ session: Object, sharedToken: { type: String, default: null } })
defineEmits(['back', 'resume', 'pause', 'edit', 'duplicate', 'converted'])
const capabilities = computed(() => sessionCapabilities(props.session))
const isLive = computed(() => ['Running', 'Pending'].includes(props.session?.phase))
// A stopped autonomous run is offered as an interactive continuation next to the plain Resume;
// the backend's own flag decides, so the card never appears where the call would answer 409.
const showConvert = computed(() => !props.sharedToken && capabilities.value.canManage && canConvert(props.session))
const converted = computed(() => convertedLabel(props.session))
// Chat sessions render the structured stream; their agent pane already replays
// history, so the raw Transcript tab stays terminal-only.
const isChat = computed(() => props.session?.uiMode === 'chat')
const activeTab = ref('agent')
watch(isChat, chat => { if (chat && activeTab.value === 'transcript') activeTab.value = 'agent' })
const shellOpened = ref(false)
const shareOpen = ref(false)
// The Transcript tab's page: null until first loaded. `source` is 'native' (role-tagged turns
// from the provider's own transcript) or 'scrollback' (cleaned terminal text, rendered through
// the heuristics in lib/transcript.js because terminal output has no roles).
const conversation = ref(null)
const transcriptItems = computed(() => toTranscriptItems(conversation.value))
const workspace = ref(null)
const statuses = reactive({ agent: 'connecting…', shell: '', transcript: '' })

// In-app approval of tool-permission requests (in addition to the messengers).
const pendingPermissions = ref([])
let permissionTimer

async function refreshPermissions() {
  if (props.sharedToken || !capabilities.value.canManage || !isLive.value) {
    pendingPermissions.value = []
    return
  }
  try { pendingPermissions.value = await api.listPermissions(props.session.id) } catch {}
}

// Fleet inbox: messages other agents of the project sent to this session. The view is
// read-only — only the agent's own inbox poll marks messages delivered — so a banner
// stays visible until the agent picks the message up or the user dismisses it. A priority
// message was pushed into the agent the moment it arrived, so it is shown for a while after
// delivery too: the person would otherwise never see what interrupted their agent.
const PRIORITY_BANNER_MS = 5 * 60 * 1000
const agentMessages = ref([])
const dismissedMessages = ref(new Set())
const visibleMessages = computed(() => agentMessages.value
  .filter(m => !dismissedMessages.value.has(m.id))
  .filter(m => !m.deliveredAt || (m.priority && Date.now() - new Date(m.deliveredAt).getTime() < PRIORITY_BANNER_MS)))

function deliveryLabel(m) {
  if (!m.priority) return ''
  if (m.deliveredVia === 'injected' || m.deliveredVia === 'mod') return 'delivered to the agent'
  return m.deliveredAt ? 'picked up from the inbox' : 'waiting in inbox'
}

async function refreshMessages() {
  if (props.sharedToken || !capabilities.value.canManage) {
    agentMessages.value = []
    return
  }
  try { agentMessages.value = await api.listSessionMessages(props.session.id) } catch {}
}

function dismissMessage(id) {
  dismissedMessages.value = new Set([...dismissedMessages.value, id])
}

// "Message this agent": the owner pushes a message into their own session. This is the only
// way to reach a chat-mode or autonomous session from the app without a terminal to type in,
// and it is what a fleet peer does with agent_send — same endpoint family, same flags.
const sendOpen = ref(false)
const sendText = ref('')
const sendPriority = ref(true)
const sendInterrupt = ref(false)
const sendBusy = ref(false)
const sendNote = ref('')
const showSendMessage = computed(() => !props.sharedToken && capabilities.value.canManage && isLive.value)
watch(sendInterrupt, stop => { if (stop) sendPriority.value = true })
watch(sendPriority, urgent => { if (!urgent) sendInterrupt.value = false })

async function sendMessage() {
  const body = sendText.value.trim()
  if (!body || sendBusy.value) return
  sendBusy.value = true
  try {
    const result = await api.sendSessionMessage(props.session.id, {
      body, priority: sendPriority.value, interrupt: sendInterrupt.value
    })
    sendText.value = ''
    sendNote.value = result?.deliveredVia === 'injected' || result?.deliveredVia === 'mod'
      ? 'Delivered to the agent.'
      : 'Waiting in the inbox — the agent reads it on its next agent_inbox call.'
    await refreshMessages()
  } catch (e) {
    sendNote.value = e?.message || 'The message could not be sent.'
  } finally {
    sendBusy.value = false
  }
}

async function decidePermission(reqId, decision) {
  pendingPermissions.value = pendingPermissions.value.filter(p => p.id !== reqId)
  try { await api.decidePermission(props.session.id, reqId, decision) } catch {}
}

// Auto approve takes effect on the running session: the backend reads the flag per
// permission request. Kept as local state so the toggle reacts immediately; the session
// list picks the new value up on its next refresh.
const autoApprove = ref(!!props.session?.autoApprove)
const autoApproveBusy = ref(false)
watch(() => props.session?.autoApprove, v => { autoApprove.value = !!v })

const autoApproveHint = computed(() => props.session?.runAsRoot
  ? 'Auto approve every tool for this session. This container runs as root, so the agent may run any command as root, unattended.'
  : 'Auto approve every tool for the rest of this session. Recommended only for non-root containers.')

async function toggleAutoApprove() {
  const next = !autoApprove.value
  autoApprove.value = next
  autoApproveBusy.value = true
  try {
    await api.updateSession(props.session.id, { autoApprove: next })
    // Enabling it clears the prompts the backend just resolved.
    await refreshPermissions()
  } catch (e) {
    autoApprove.value = !next
  } finally {
    autoApproveBusy.value = false
  }
}

// Provider account of a running Subscription session. The owner can move it to another of
// their stored logins; the pod swaps the file and restarts the agent with resume, so the
// conversation continues under the other account (docs/provider-accounts.md).
const providerAccounts = ref({})
const accountList = computed(() => accountsFor(providerAccounts.value, props.session?.agent))
// What the session is known to run on right now. The parent's session object only picks the
// new id up on its next refresh, so a switch made here is remembered until then.
const switchedTo = ref('')
const currentAccountId = computed(() =>
  switchedTo.value || props.session?.credentialId || defaultAccountId(accountList.value))
const pendingAccountId = ref('')
const pendingAccount = computed(() =>
  accountList.value.find(a => a.id === pendingAccountId.value && a.id !== currentAccountId.value) || null)
const accountBusy = ref(false)
const accountNote = ref('')
const showAccountSwitch = computed(() => !props.sharedToken && capabilities.value.canManage
  && props.session?.phase === 'Running' && props.session?.authMode === 'Subscription' && accountList.value.length >= 2)

async function loadAccounts() {
  if (props.sharedToken) return
  try { providerAccounts.value = (await api.listProviderAccounts()) || {} } catch { providerAccounts.value = {} }
}

function chooseAccount(event) {
  pendingAccountId.value = event.target.value
  accountNote.value = ''
}

async function confirmAccountSwitch() {
  const target = pendingAccount.value
  if (!target || accountBusy.value) return
  accountBusy.value = true
  try {
    await api.switchSessionCredential(props.session.id, target.id)
    switchedTo.value = target.id
    pendingAccountId.value = ''
    accountNote.value = `Switched to “${target.label}” — the agent restarts and resumes the conversation.`
  } catch (e) {
    // The choice is dropped with the failure: the dropdown falls back to the account the session
    // still runs on, and the reason is shown where the confirmation was instead of under it.
    pendingAccountId.value = ''
    accountNote.value = e?.message || 'The account could not be switched.'
  } finally {
    accountBusy.value = false
  }
}

onMounted(() => {
  refreshPermissions()
  refreshMessages()
  loadAccounts()
  permissionTimer = setInterval(() => { refreshPermissions(); refreshMessages(); refreshTranscript() }, 4000)
})
onBeforeUnmount(() => clearInterval(permissionTimer))
watch(() => props.session?.id, () => {
  pendingPermissions.value = []
  agentMessages.value = []
  dismissedMessages.value = new Set()
  conversation.value = null
  switchedTo.value = ''
  pendingAccountId.value = ''
  accountNote.value = ''
  refreshPermissions()
  refreshMessages()
  loadAccounts()
  if (activeTab.value === 'transcript') loadTranscript()
})
// The session agent uploads once more as it exits; one final fetch after the phase settles
// picks that tail up instead of leaving the tab on the last live poll.
watch(isLive, (live, wasLive) => { if (!live && wasLive) refreshTranscript(true) })
watch(() => props.session?.credentialId, id => { if (id && id === switchedTo.value) switchedTo.value = '' })

const repoLabel = computed(() => repoShortName(props.session?.repoUrl || props.session?.repos?.[0]?.url || ''))

async function fetchConversation(offset) {
  return props.sharedToken
    ? getSharedConversation(props.sharedToken, offset)
    : api.getConversation(props.session.id, offset)
}

async function loadTranscript() {
  const sessionId = props.session?.id
  try {
    const page = await fetchConversation()
    if (sessionId !== props.session?.id) return
    conversation.value = conversationState(page)
  } catch {
    if (sessionId !== props.session?.id) return
    conversation.value = { source: 'scrollback', entries: [], text: '', nextOffset: 0, length: 0 }
  }
}

// Follows a running session from the cursor the last page left, so the tab shows what the
// agent is doing now rather than what it had done when the tab was opened. Polling, not the
// event socket: the transcript is appended by a 30-second upload, and a cursor poll costs the
// hub one small page while a push would still need the same read to find out what is new.
async function refreshTranscript(force = false) {
  const current = conversation.value
  if (!current || activeTab.value !== 'transcript' || (!isLive.value && !force)) return
  const sessionId = props.session?.id
  try {
    const page = await fetchConversation(current.nextOffset)
    if (sessionId !== props.session?.id || conversation.value !== current) return
    const merged = mergeConversationPage(current, page)
    if (merged) conversation.value = merged
    else await loadTranscript()
  } catch {}
}

async function selectTab(tab) {
  if (tab === 'shell') shellOpened.value = true
  activeTab.value = tab
  if (tab === 'transcript' && conversation.value === null) await loadTranscript()
}
</script>
<template>
  <div class="term-wrap">
    <div class="term-bar">
      <button v-if="!sharedToken" class="back" title="Back" @click="$emit('back')">‹</button>
      <div class="head-main">
        <div class="title">{{ session.title }}</div>
        <div class="meta">
          <span class="st" :style="{ color: statusStyle(session).color }">{{ sessionStatus(session) }}</span>
          <span class="mdot" :style="{ background: statusStyle(session).color }"></span>
          <span v-if="session.mode">{{ session.mode }}</span>
          <span v-if="converted" class="converted" data-converted-from>({{ converted }})</span>
          <span v-if="session.agent">· {{ session.agent }}<template v-if="session.authMode"> / {{ authLabel(session.authMode) }}</template></span>
          <span v-if="repoLabel" class="mono">· {{ repoLabel }}</span>
          <span v-if="session.schedule" class="cron">· ▶ {{ session.schedule }}</span>
          <span v-if="session.expiresAt" class="expires" :class="{ soon: isExpiringSoon(session.expiresAt) }"
            data-expires-at :title="`Auto-delete ${session.autoDeleteFrom === 'start' ? 'after start' : 'after last activity'}`">· ⌛ expires in {{ formatRemaining(session.expiresAt) }}</span>
          <span v-if="session.sharedBy" class="shared">· shared by {{ session.sharedBy }}</span>
        </div>
      </div>
      <label v-if="showAccountSwitch" class="acct" data-account-switch>
        <span class="acct-label">Account</span>
        <select data-account-select :value="pendingAccountId || currentAccountId" :disabled="accountBusy"
          aria-label="Provider account of this session" @change="chooseAccount">
          <option v-for="a in accountList" :key="a.id" :value="a.id" :data-account-option="a.id">{{ accountOptionLabel(a) }}</option>
        </select>
      </label>
      <button class="bar-btn" data-open-files @click="workspace?.openFiles()">Files</button>
      <nav class="tabs">
        <button :class="{ on: activeTab === 'agent' }" @click="selectTab('agent')">{{ tabLabel('agent') }}</button>
        <button v-if="isLive && capabilities.canShell" :class="{ on: activeTab === 'shell' }" @click="selectTab('shell')">{{ tabLabel('shell') }}</button>
        <button v-if="!isChat" :class="{ on: activeTab === 'transcript' }" @click="selectTab('transcript')">Transcript</button>
      </nav>
      <span v-if="!capabilities.canWrite" class="readonly">Read-only</span>
      <template v-if="capabilities.canManage">
        <button v-if="canPause(session)" class="bar-btn" @click="$emit('pause', session.id)">❚❚ Pause</button>
        <button v-if="session.canResume" class="bar-btn" @click="$emit('resume', session.id)">▶ Resume</button>
        <button v-if="showSendMessage" class="bar-btn" data-send-message-toggle :class="{ on: sendOpen }" @click="sendOpen = !sendOpen">✉ Message</button>
        <button class="bar-btn" @click="$emit('edit', session.id)">✎ Edit session</button>
        <button class="bar-btn primary" @click="shareOpen = !shareOpen">↗ Share</button>
      </template>
      <span class="status">{{ statuses[activeTab] }}</span>
      <div v-if="shareOpen" class="share-pop">
        <div class="share-head"><span>Share session</span><button class="ghost" @click="shareOpen = false">✕</button></div>
        <ShareSessionDialog embedded :session="session" @close="shareOpen = false" />
      </div>
    </div>
    <ConvertSessionCard v-if="showConvert" :session="session" @converted="$emit('converted', $event)" />
    <div v-if="pendingAccount" class="perm acct-confirm" data-account-confirm>
      <span class="ask-dot"></span>
      <div class="perm-text">
        <strong>Switch this session to “{{ pendingAccount.label }}”?</strong>
        <span class="perm-summary">The agent restarts with the other login and resumes the conversation.
          <template v-if="pendingAccount.email || pendingAccount.organization"> {{ [pendingAccount.email, pendingAccount.organization].filter(Boolean).join(' · ') }}</template></span>
      </div>
      <div class="perm-actions">
        <button class="bar-btn primary" data-account-confirm-switch :disabled="accountBusy" @click="confirmAccountSwitch">{{ accountBusy ? 'Switching…' : 'Switch' }}</button>
        <button class="bar-btn" data-account-cancel :disabled="accountBusy" @click="pendingAccountId = ''">Cancel</button>
      </div>
    </div>
    <div v-else-if="accountNote" class="perm acct-note" data-account-note>
      <span class="ask-dot msg-dot"></span>
      <div class="perm-text"><span class="msg-body">{{ accountNote }}</span></div>
      <div class="perm-actions"><button class="bar-btn" data-account-note-dismiss @click="accountNote = ''">Dismiss</button></div>
    </div>
    <div v-if="session.questionPending && capabilities.canWrite" class="asking">
      <span class="ask-dot"></span>THE AGENT IS ASKING — reply {{ isChat ? 'below' : 'in the terminal below' }}.
    </div>
    <div v-if="autoApprove && isLive && capabilities.canManage && !sharedToken" class="perm auto-on">
      <span class="ask-dot"></span>
      <div class="perm-text">
        <strong>Auto approve is on — tools run without asking.</strong>
        <span class="perm-summary">
          {{ session.runAsRoot
            ? 'This container runs as root, so the agent may run any command as root, unattended.'
            : 'Recommended only for non-root containers.' }}
        </span>
      </div>
      <div class="perm-actions">
        <button class="bar-btn" data-auto-approve :disabled="autoApproveBusy" @click="toggleAutoApprove">Turn off</button>
      </div>
    </div>
    <div v-if="showSendMessage && sendOpen" class="perm send-card" data-send-message>
      <span class="ask-dot send-dot"></span>
      <div class="perm-text send-form">
        <strong>Message this agent</strong>
        <textarea v-model="sendText" rows="2" maxlength="4000" placeholder="What should the agent know or do?"
          data-send-message-text :disabled="sendBusy" @keydown.ctrl.enter.prevent="sendMessage"></textarea>
        <div class="send-flags">
          <label><input type="checkbox" v-model="sendPriority" data-send-message-priority :disabled="sendBusy" /> priority — deliver into the running prompt now</label>
          <label><input type="checkbox" v-model="sendInterrupt" data-send-message-interrupt :disabled="sendBusy" /> interrupt — stop the current work first</label>
        </div>
        <span v-if="sendNote" class="perm-summary send-note" data-send-message-note>{{ sendNote }}</span>
      </div>
      <div class="perm-actions">
        <button class="bar-btn primary" data-send-message-submit :disabled="sendBusy || !sendText.trim()" @click="sendMessage">{{ sendBusy ? 'Sending…' : 'Send' }}</button>
        <button class="bar-btn" data-send-message-close :disabled="sendBusy" @click="sendOpen = false; sendNote = ''">Close</button>
      </div>
    </div>
    <div v-for="m in visibleMessages" :key="m.id" class="perm agent-msg" :class="{ priority: m.priority }" data-agent-message
         :data-priority="m.priority ? 'true' : null" :data-delivered-via="m.deliveredVia || null">
      <span class="ask-dot msg-dot"></span>
      <div class="perm-text">
        <strong>{{ m.interrupt ? 'Interrupt' : m.priority ? 'Priority message' : 'Message' }} from {{ m.fromTitle ? `agent “${m.fromTitle}”` : 'outside the fleet' }}<template v-if="m.priority"> · <span class="msg-delivery" data-message-delivery>{{ deliveryLabel(m) }}</span></template></strong>
        <span class="msg-body">{{ m.body }}</span>
      </div>
      <div class="perm-actions">
        <button class="bar-btn" data-dismiss-message @click="dismissMessage(m.id)">Dismiss</button>
      </div>
    </div>
    <div v-for="p in pendingPermissions" :key="p.id" class="perm">
      <span class="ask-dot"></span>
      <div class="perm-text">
        <strong>{{ permissionTitle(p.tool) }}</strong>
        <span v-if="p.summary" class="perm-summary">{{ p.summary }}</span>
      </div>
      <div class="perm-actions">
        <button class="bar-btn primary" @click="decidePermission(p.id, 'allow')">Allow</button>
        <button class="bar-btn" @click="decidePermission(p.id, 'allowAlways')">Allow (don't ask again)</button>
        <button v-if="capabilities.canManage && !sharedToken" class="bar-btn" data-auto-approve
                :disabled="autoApproveBusy" :title="autoApproveHint" @click="toggleAutoApprove">Allow everything</button>
        <button class="bar-btn danger" @click="decidePermission(p.id, 'deny')">Deny</button>
      </div>
    </div>
    <SessionWorkspace ref="workspace" :session="session" :can-write="capabilities.canWrite" :shared-token="sharedToken">
      <div class="terminal-stack">
        <ChatPane v-if="isChat" v-show="activeTab === 'agent'" :session="session" :shared-token="sharedToken" :readonly="!capabilities.canWrite" :active="activeTab === 'agent'" @status="statuses.agent = $event" />
        <TerminalPane v-else v-show="activeTab === 'agent'" :session="session" :shared-token="sharedToken" :readonly="!capabilities.canWrite" kind="agent" :active="activeTab === 'agent'" @status="statuses.agent = $event" />
        <TerminalPane v-if="isLive && capabilities.canShell && shellOpened" v-show="activeTab === 'shell'" :session="session" kind="shell" :active="activeTab === 'shell'" @status="statuses.shell = $event" />
        <section v-if="activeTab === 'transcript'" class="transcript" aria-labelledby="transcript-heading">
          <div class="transcript-inner">
            <h3 id="transcript-heading">What happened so far</h3>
            <p v-if="conversation === null" class="transcript-state">Loading…</p>
            <p v-else-if="!transcriptItems.length" class="transcript-state">[no saved transcript]</p>
            <ol v-else class="transcript-list" :aria-label="conversation.source === 'native' ? 'Conversation' : 'Terminal transcript'"
                :data-transcript-source="conversation.source">
              <li v-for="(item, index) in transcriptItems" :key="index" class="transcript-bubble" :class="'role-' + item.role"
                  :data-transcript-role="item.role">
                <span class="transcript-label">{{ item.label }}</span>
                <pre>{{ item.text }}</pre>
              </li>
            </ol>
          </div>
        </section>
      </div>
    </SessionWorkspace>
  </div>
</template>
<style scoped>
.term-wrap { flex: 1; display: flex; flex-direction: column; min-width: 0; min-height: 0; background: #0e0d0b; }
.term-bar { display: flex; align-items: center; gap: 10px; padding: 10px 16px; border-bottom: 1px solid var(--border); background: var(--bg); position: relative; flex-wrap: wrap; }
.back { width: 30px; height: 30px; display: flex; align-items: center; justify-content: center; border-radius: 9px; border: none; background: none; color: var(--muted); font-size: 16px; padding: 0; flex-shrink: 0; }
.back:hover { background: var(--panel-2); color: var(--text); }
.head-main { min-width: 0; flex: 1; }
.title { font-family: var(--display); font-size: 17px; font-weight: 700; color: var(--strong); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.meta { display: flex; align-items: center; gap: 7px; margin-top: 2px; font-size: 11px; color: var(--muted-3); white-space: nowrap; overflow: hidden; }
.st { font-weight: 700; }
.mdot { width: 7px; height: 7px; border-radius: 50%; flex-shrink: 0; }
.mono { font-family: var(--mono); }
.cron { color: var(--sched); font-weight: 600; }
.expires { color: var(--muted); font-weight: 600; }
.expires.soon { color: var(--warn); }
.shared { color: var(--accent); }
.converted { color: var(--faint); }
.tabs { display: flex; gap: 2px; background: var(--panel); border: 1px solid var(--border-2); border-radius: var(--radius); padding: 3px; }
.tabs button { font-size: 12px; font-weight: 700; padding: 5px 14px; border-radius: 8px; border: none; background: none; color: var(--muted-3); }
.tabs button:hover { color: var(--text); background: none; }
.tabs button.on { background: var(--border-2); color: var(--strong); }
.bar-btn { font-size: 12px; padding: 6px 14px; border-radius: 9px; white-space: nowrap; }
.acct { display: flex; align-items: center; gap: 8px; margin: 0; font-size: 11px; color: var(--muted-3); }
.acct select { width: auto; max-width: 260px; padding: 5px 10px; font-size: 12px; }
.acct-note { color: var(--accent-2); background: #121a24; border-bottom: 1px solid #24405c; }
.readonly { font-size: 12px; }
.status { color: var(--muted-3); font: 11px var(--mono); }
.share-pop { position: absolute; top: 100%; right: 16px; margin-top: 8px; width: min(560px, calc(100vw - 48px)); max-height: 70vh; overflow-y: auto; background: var(--panel); border: 1px solid var(--border-3); border-radius: var(--radius-lg); box-shadow: 0 16px 48px rgba(0,0,0,0.5); z-index: 50; }
.share-head { display: flex; align-items: center; justify-content: space-between; padding: 14px 20px 0; font-family: var(--display); font-weight: 700; color: var(--strong); }
.asking { display: flex; align-items: center; gap: 8px; padding: 10px 20px; font-weight: 700; color: var(--warn); font-size: 12px; background: #1f1b12; border-bottom: 1px solid #4a3e1e; }
.ask-dot { width: 7px; height: 7px; border-radius: 50%; background: var(--warn); flex-shrink: 0; }
.perm { display: flex; align-items: center; gap: 10px; padding: 10px 20px; font-size: 12px; color: var(--warn); background: #1f1b12; border-bottom: 1px solid #4a3e1e; flex-wrap: wrap; }
.perm-text { display: flex; flex-direction: column; gap: 2px; min-width: 0; flex: 1; }
.perm-summary { color: var(--muted-3); font-family: var(--mono); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.perm-actions { display: flex; gap: 8px; }
.perm-actions .danger { color: #e5484d; }
.agent-msg { color: var(--accent-2); background: #121a24; border-bottom: 1px solid #24405c; }
.msg-dot { background: var(--accent-2); }
.agent-msg.priority { color: var(--accent); background: #0f1d2e; border-left: 3px solid var(--accent); border-bottom-color: var(--accent); }
.agent-msg.priority .msg-dot { background: var(--accent); }
.msg-delivery { font-weight: 400; color: var(--muted-3); }
.send-card { color: var(--text); background: var(--panel); border-bottom: 1px solid var(--border-2); align-items: flex-start; }
.send-dot { background: var(--accent); margin-top: 6px; }
.send-form { gap: 8px; }
.send-form textarea { width: 100%; resize: vertical; font: 13px/1.5 var(--mono); }
.send-flags { display: flex; flex-wrap: wrap; gap: 6px 18px; font-size: 12px; color: var(--muted); }
.send-flags label { display: flex; align-items: center; gap: 6px; margin: 0; }
.send-flags input { width: auto; margin: 0; }
.send-note { white-space: normal; color: var(--muted); }
.bar-btn.on { background: var(--border-2); color: var(--strong); }
.msg-body { color: var(--muted); white-space: pre-wrap; overflow-wrap: anywhere; }
.terminal-stack { flex: 1; display: flex; flex-direction: column; min-width: 0; min-height: 0; }
.transcript { flex: 1; overflow-y: auto; min-height: 0; background: var(--bg); }
.transcript-inner { max-width: 760px; margin: 0 auto; padding: 26px 24px; }
.transcript-inner h3 { font-size: 20px; margin: 0 0 14px; }
.transcript-list { display: flex; flex-direction: column; gap: 12px; list-style: none; margin: 0; padding: 0; }
.transcript-bubble { padding: 12px 14px 14px; background: var(--panel); border: 1px solid var(--border-2); border-left: 3px solid var(--accent); border-radius: 12px; }
.transcript-bubble.role-user { border-left-color: var(--accent-2); background: var(--panel-2); }
.transcript-bubble.role-tool, .transcript-bubble.role-result { border-left-color: var(--border-3); }
.transcript-bubble.role-tool pre, .transcript-bubble.role-result pre { color: var(--muted); font-size: 12px; }
.transcript-label { display: block; color: var(--muted-2); font: 700 10px/1 var(--display); letter-spacing: .08em; text-transform: uppercase; }
.transcript-bubble pre { margin: 7px 0 0; white-space: pre-wrap; overflow-wrap: anywhere; font: 13px/1.6 var(--mono); color: #c9c4bb; }
.transcript-state { margin: 0; color: var(--muted-3); font: 13px/1.6 var(--mono); }
</style>
