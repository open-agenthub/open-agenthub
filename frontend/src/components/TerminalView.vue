<script setup>
import SessionIcon from './SessionIcon.vue'
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import TerminalPane from './TerminalPane.vue'
import SessionWorkspace from './SessionWorkspace.vue'
import WorkspaceStream from './workspace/WorkspaceStream.vue'
import ShareSessionDialog from './ShareSessionDialog.vue'
import ConversationTimeline from './workspace/ConversationTimeline.vue'
import WorkspaceComposer from './workspace/WorkspaceComposer.vue'
import CliMenuNotice from './workspace/CliMenuNotice.vue'
import { CANCEL_KEYS, keysForOption } from '../lib/cli-menu.js'
import ConvertSessionCard from './ConvertSessionCard.vue'
import { canPause, sessionStatus, statusStyle, tabLabel } from '../lib/status.js'
import { sessionCapabilities } from '../lib/access.js'
import { canConvert, convertedLabel } from '../lib/conversion.js'
import { api, getSharedConversation } from '../api.js'
import { repoShortName } from '../lib/text.js'
import { accountLimitLabel, accountOptionLabel, accountsFor, authLabel, availableAlternatives, defaultAccountId, isAccountExhausted } from '../lib/agent.js'
import { conversationState, mergeConversationPage, toTranscriptItems } from '../lib/transcript.js'
import { permissionTitle } from '../lib/permissions.js'
import { formatRemaining, isExpiringSoon } from '../lib/expiry.js'
import { preferredUi } from '../lib/ui-preference.js'
import SecretRequestBanner from './SecretRequestBanner.vue'

const props = defineProps({ session: Object, sharedToken: { type: String, default: null }, initialUi: { type: String, default: null } })
const emit = defineEmits(['back', 'resume', 'pause', 'edit', 'duplicate', 'converted'])
const capabilities = computed(() => sessionCapabilities(props.session))
const isLive = computed(() => ['Running', 'Pending'].includes(props.session?.phase))
// Reopens a browser pane the person closed; it stays offered while the pane is open because on a
// phone it is also the quickest way back to the browser from the agent.
const browserRunning = computed(() => (props.session?.browser?.phase || 'Stopped') !== 'Stopped')
// A stopped autonomous run is offered as an interactive continuation next to the plain Resume;
// the backend's own flag decides, so the card never appears where the call would answer 409.
const showConvert = computed(() => !props.sharedToken && capabilities.value.canManage && canConvert(props.session))
const converted = computed(() => convertedLabel(props.session))
// Older 'chat' records use the streaming transport, now rendered only in Workspace.
const isChat = computed(() => props.session?.uiMode === 'chat')
const activeTab = ref(isChat.value || (props.initialUi || preferredUi()) === 'workspace' ? 'workspace' : 'agent')
const modern = computed(() => activeTab.value === 'workspace')
const workspaceOpened = ref(activeTab.value === 'workspace')
// The Workspace is the one place the saved conversation is shown; a separate Transcript tab
// duplicated it and was removed (#113). Streaming sessions render their own frames instead.
const conversationVisible = computed(() => modern.value && !isChat.value)
const terminalOpened = ref(false)
const drawerKind = ref('agent')
// What the agent CLI shows of its own interactive screens (#112), and whether the drawer is open
// because of it: only a drawer this view opened for a menu is closed again when the menu goes.
const cliMenu = ref(null)
let drawerOpenedForMenu = false
const agentTerminal = ref(null)
const composer = ref(null)
const transcriptError = ref('')
const transcriptBusy = ref(false)
const canTask = computed(() => capabilities.value.canWrite && isLive.value &&
  String(props.session?.mode).toLowerCase() === 'interactive')
const canCompose = computed(() => canTask.value && statuses.agent === 'connected')
watch(isChat, chat => { if (chat) activeTab.value = 'workspace' })
const shellOpened = ref(false)
const shareOpen = ref(false)
// The Workspace conversation's page: null until first loaded. `source` is 'native' (role-tagged turns
// from the provider's own transcript) or 'scrollback' (cleaned terminal text, rendered through
// the heuristics in lib/transcript.js because terminal output has no roles).
const conversation = ref(null)
const transcriptItems = computed(() => toTranscriptItems(conversation.value))
const workspace = ref(null)
const statuses = reactive({ agent: 'connecting…', shell: '' })

// In-app approval of tool-permission requests (in addition to the messengers).
const pendingPermissions = ref([])
let permissionTimer
let conversationTimer
const CONVERSATION_POLL_MS = 1000

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
// Messages live in the ✉ panel, not above the terminal: a fleet of chatty peers used to stack
// one banner per message over the agent's output until nothing underneath was readable. The
// button carries a count of the open ones instead, and the panel lists them on demand.
const visibleMessages = computed(() => agentMessages.value
  .filter(m => !dismissedMessages.value.has(m.id))
  .filter(m => !m.deliveredAt || (m.priority && Date.now() - new Date(m.deliveredAt).getTime() < PRIORITY_BANNER_MS)))
// The badge counts exactly what the panel lists — a number on the button that does not match
// the tiles behind it would only make people hunt for the missing one.
const expandedMessages = ref(new Set())
const isExpanded = id => expandedMessages.value.has(id)
function toggleExpanded(id) {
  const next = new Set(expandedMessages.value)
  if (next.has(id)) next.delete(id); else next.add(id)
  expandedMessages.value = next
}
// Tiles show three lines; a longer body gets a "Show more". Line count is the honest signal,
// the length a fallback for one long paragraph that wraps past three lines anyway.
const CLAMP_LINES = 3
const CLAMP_CHARS = 240
const needsClamp = m => m.body.split('\n').length > CLAMP_LINES || m.body.length > CLAMP_CHARS
function messageTime(m) {
  const at = new Date(m.createdAt)
  if (Number.isNaN(at.getTime())) return ''
  const sameDay = at.toDateString() === new Date().toDateString()
  return sameDay ? at.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
    : at.toLocaleString([], { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' })
}

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
// The panel (list + badge) is for every manager of the session; sending needs a live pod.
const showMessages = computed(() => !props.sharedToken && capabilities.value.canManage)
const showSendMessage = computed(() => showMessages.value && isLive.value)
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
// The pin, else what the last start mounted (the hub records it), else the default.
const currentAccountId = computed(() =>
  switchedTo.value || props.session?.credentialId || props.session?.resolvedCredentialId || defaultAccountId(accountList.value))
const currentAccount = computed(() => accountList.value.find(a => a.id === currentAccountId.value) || null)
// The hub moved the session on its own (docs/account-limits.md): the record's account changed
// under a running session without this view asking for it. Shown until dismissed; a switch
// made from the dropdown here is reported through accountNote instead.
const autoSwitched = ref(null)
watch(() => props.session?.credentialId, (id, previous) => {
  if (!id || !previous || id === previous || id === switchedTo.value || !isLive.value) return
  autoSwitched.value = id
})
const autoSwitchedLabel = computed(() => {
  const account = accountList.value.find(a => a.id === autoSwitched.value)
  return account ? accountOptionLabel(account) : autoSwitched.value
})
// The account the session runs on is at its usage limit. Whether another one could take
// over is what the person wants to know — the hub has already tried (or was told not to).
const accountAtLimit = computed(() => !props.sharedToken && capabilities.value.canManage && isLive.value
  && props.session?.authMode === 'Subscription' && isAccountExhausted(currentAccount.value))
const hasAlternative = computed(() => availableAlternatives(accountList.value, currentAccountId.value).length > 0)
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
  if (conversationVisible.value) void loadTranscript()
  refreshPermissions()
  refreshMessages()
  loadAccounts()
  permissionTimer = setInterval(() => { refreshPermissions(); refreshMessages() }, 4000)
  conversationTimer = setInterval(() => refreshTranscript(), CONVERSATION_POLL_MS)
})
onBeforeUnmount(() => { clearInterval(permissionTimer); clearInterval(conversationTimer) })
watch(() => props.session?.id, () => {
  pendingPermissions.value = []
  agentMessages.value = []
  dismissedMessages.value = new Set()
  conversation.value = null
  transcriptError.value = ''
  terminalOpened.value = false
  cliMenu.value = null
  drawerOpenedForMenu = false
  shellOpened.value = false
  drawerKind.value = 'agent'
  statuses.agent = 'connecting…'
  switchedTo.value = ''
  pendingAccountId.value = ''
  accountNote.value = ''
  autoSwitched.value = null
  refreshPermissions()
  refreshMessages()
  loadAccounts()
  if (conversationVisible.value) loadTranscript()
})
// The session agent uploads once more as it exits; one final fetch after the phase settles
// picks that tail up instead of leaving the conversation on the last live poll.
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
  const token = props.sharedToken
  transcriptBusy.value = true
  transcriptError.value = ''
  try {
    const page = await fetchConversation()
    if (sessionId !== props.session?.id || token !== props.sharedToken) return
    conversation.value = conversationState(page)
    // Finished conversations can span more than one page too. Drain the saved history
    // without waiting for the live-session poll, which never runs after completion.
    while (conversation.value.nextOffset < conversation.value.length) {
      const current = conversation.value
      const next = await fetchConversation(current.nextOffset)
      if (sessionId !== props.session?.id || token !== props.sharedToken || conversation.value !== current) return
      const merged = mergeConversationPage(current, next)
      if (!merged || merged.nextOffset <= current.nextOffset) break
      conversation.value = merged
    }
  } catch {
    if (sessionId !== props.session?.id) return
    transcriptError.value = 'Could not load the conversation.'
  } finally {
    if (sessionId === props.session?.id && token === props.sharedToken) transcriptBusy.value = false
  }
}

// Follows a running session from the cursor the last page left, so the Workspace shows what the
// agent is doing now rather than what it had done when it was opened. The hub reads a running
// session's transcript from its pod by byte offset (#111), so a poll costs one small page, and
// once a second keeps the view within the one to two seconds of the terminal the issue asks for.
// A hidden browser tab skips its polls; the next visible one catches up from the same cursor.
async function refreshTranscript(force = false) {
  const current = conversation.value
  if (transcriptBusy.value || !conversationVisible.value || (!isLive.value && !force)) return
  if (!force && typeof document !== 'undefined' && document.visibilityState === 'hidden') return
  if (!current) return loadTranscript()
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
  if (tab === 'workspace') workspaceOpened.value = true
  if (tab === 'shell') shellOpened.value = true
  activeTab.value = tab
  if (conversationVisible.value && conversation.value === null && !transcriptBusy.value) await loadTranscript()
}

// The session keeps its id, so this view is not remounted and initialUi is not read again:
// the surface the person picked on the card is selected here.
function onConverted(id, surface) {
  selectTab(surface === 'terminal' ? 'agent' : 'workspace')
  emit('converted', id, surface)
}

function onCliMenu(menu) {
  cliMenu.value = menu
  if (menu && modern.value && !(terminalOpened.value && drawerKind.value === 'agent')) {
    terminalOpened.value = true
    drawerKind.value = 'agent'
    drawerOpenedForMenu = true
  } else if (!menu && drawerOpenedForMenu) {
    terminalOpened.value = false
    drawerOpenedForMenu = false
  }
  // The composer's "answer the menu first" is about this menu; it goes when the menu does.
  if (!menu) composer.value?.clearError?.()
}
function answerCliMenu(number) {
  if (cliMenu.value) void agentTerminal.value?.sendKeys(keysForOption(cliMenu.value, number)).catch(() => {})
}
function cancelCliMenu() {
  void agentTerminal.value?.sendKeys([CANCEL_KEYS]).catch(() => {})
}
function toggleDrawer(kind) {
  // The person took the drawer over; a menu closing must not shut it under them.
  drawerOpenedForMenu = false
  if (kind === 'shell') shellOpened.value = true
  terminalOpened.value = !(terminalOpened.value && drawerKind.value === kind)
  drawerKind.value = kind
}
function sendWorkspaceMessage(text) {
  if (!canCompose.value || !agentTerminal.value) {
    throw Object.assign(new Error('Agent is disconnected. Your draft is still here.'), { retryable: true })
  }
  // A message pasted into one of the CLI's own screens is not a message: its Enter picks the
  // highlighted option, or submits the text as a login code. Refused, so the draft stays.
  if (cliMenu.value) {
    throw new Error(cliMenu.value.kind === 'choice'
      ? 'The agent is showing a menu. Answer it above or in the terminal first; your draft is still here.'
      : 'The agent is waiting for input in its terminal. Answer it there first; your draft is still here.')
  }
  return agentTerminal.value.submitMessage(text)
}
function interruptWorkspaceAgent() {
  if (!canCompose.value || !agentTerminal.value) throw new Error('Agent is disconnected')
  return agentTerminal.value.interruptAgent()
}
</script>
<template>
  <div class="term-wrap" :class="{ 'modern-workspace': modern }">
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
            data-expires-at :title="`Auto-delete ${session.autoDeleteFrom === 'start' ? 'after start' : 'after last activity'}`">· <SessionIcon name="expiry" /> expires in {{ formatRemaining(session.expiresAt) }}</span>
          <span v-if="session.sharedBy" class="shared">· shared by {{ session.sharedBy }}</span>
        </div>
      </div>
      <label v-if="showAccountSwitch" class="acct" data-account-switch>
        <span class="acct-label">Account</span>
        <select data-account-select :value="pendingAccountId || currentAccountId" :disabled="accountBusy"
          aria-label="Provider account of this session" @change="chooseAccount">
          <option v-for="a in accountList" :key="a.id" :value="a.id" :data-account-option="a.id"
            :data-account-exhausted="isAccountExhausted(a) ? 'true' : null">{{ accountOptionLabel(a) }}{{ isAccountExhausted(a) ? ` (${accountLimitLabel(a)})` : '' }}</option>
        </select>
      </label>
      <button class="bar-btn" data-open-files @click="workspace?.openFiles()">Files</button>
      <button v-if="browserRunning" class="bar-btn" data-open-browser @click="workspace?.openBrowser()">Browser</button>
      <nav class="tabs">
        <button :class="{ on: modern }" data-open-workspace @click="selectTab('workspace')">Workspace</button>
        <button v-if="!isChat" :class="{ on: activeTab === 'agent' }" @click="selectTab('agent')">Terminal</button>
        <button v-if="isLive && capabilities.canShell" :class="{ on: activeTab === 'shell' }" @click="selectTab('shell')">{{ tabLabel('shell') }}</button>
      </nav>
      <span v-if="!capabilities.canWrite" class="readonly">Read-only</span>
      <template v-if="capabilities.canManage">
        <button v-if="canPause(session)" class="bar-btn" @click="$emit('pause', session.id)">❚❚ Pause</button>
        <button v-if="session.canResume" class="bar-btn" @click="$emit('resume', session.id)">▶ Resume</button>
        <button v-if="showMessages" class="bar-btn" data-send-message-toggle :class="{ on: sendOpen }" @click="sendOpen = !sendOpen">✉ Messages<span v-if="visibleMessages.length" class="msg-badge" data-message-badge>{{ visibleMessages.length }}</span></button>
        <button class="bar-btn" @click="$emit('edit', session.id)">✎ Edit session</button>
        <button class="bar-btn primary" @click="shareOpen = !shareOpen">↗ Share</button>
      </template>
      <span class="status">{{ modern ? statuses.agent : statuses[activeTab] }}</span>
      <div v-if="shareOpen" class="share-pop">
        <div class="share-head"><span>Share session</span><button class="ghost" @click="shareOpen = false">✕</button></div>
        <ShareSessionDialog embedded :session="session" @close="shareOpen = false" />
      </div>
    </div>
    <ConvertSessionCard v-if="showConvert" :session="session" @converted="onConverted" />
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
    <div v-if="autoSwitched" class="perm acct-note" data-account-switched>
      <span class="ask-dot msg-dot"></span>
      <div class="perm-text">
        <strong>Account switched to “{{ autoSwitchedLabel }}”.</strong>
        <span class="perm-summary">The previous account hit its usage limit; the agent restarted on this one and resumed the conversation.</span>
      </div>
      <div class="perm-actions"><button class="bar-btn" data-account-switched-dismiss @click="autoSwitched = null">Dismiss</button></div>
    </div>
    <div v-if="accountAtLimit" class="perm" data-account-at-limit :data-has-alternative="hasAlternative ? 'true' : 'false'">
      <span class="ask-dot"></span>
      <div class="perm-text">
        <strong>Account at limit{{ hasAlternative ? '' : ', no other account available' }}.</strong>
        <span class="perm-summary">
          {{ hasAlternative
            ? (session.accountFailover === 'off'
              ? 'Automatic switching is off for this session — pick another account from the dropdown.'
              : 'Another account is available — pick it from the dropdown if the hub has not switched yet.')
            : `“${currentAccount?.label}” ${accountLimitLabel(currentAccount)}. The session keeps running on it; sign in with another login or wait for the reset.` }}
        </span>
      </div>
    </div>
    <div v-if="session.questionPending && capabilities.canWrite" class="asking">
      <span class="ask-dot"></span>THE AGENT IS ASKING — reply {{ modern || isChat ? 'below' : 'in the terminal below' }}.
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
    <div v-if="showMessages && sendOpen" class="messages-panel" data-send-message>
      <div class="messages-head">
        <strong>Messages</strong>
        <span class="perm-summary" data-messages-count>{{ visibleMessages.length ? `${visibleMessages.length} open` : 'nothing open' }}</span>
        <button class="ghost" data-send-message-close :disabled="sendBusy" @click="sendOpen = false; sendNote = ''">✕</button>
      </div>
      <div v-if="showSendMessage" class="perm send-card" data-send-message-form>
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
        </div>
      </div>
      <div v-if="visibleMessages.length" class="messages-grid">
        <article v-for="m in visibleMessages" :key="m.id" class="msg-tile" :class="{ priority: m.priority, interrupt: m.interrupt }" data-agent-message
             :data-priority="m.priority ? 'true' : null" :data-delivered-via="m.deliveredVia || null">
          <header class="msg-tile-head">
            <span class="msg-from"><span class="msg-kind" :class="{ priority: m.priority }">{{ m.interrupt ? 'Interrupt' : m.priority ? 'Priority message' : 'Message' }}</span> from {{ m.fromTitle ? `agent “${m.fromTitle}”` : 'outside the fleet' }}</span>
            <time class="msg-time" :datetime="m.createdAt">{{ messageTime(m) }}</time>
            <button class="ghost msg-dismiss" title="Dismiss" aria-label="Dismiss message" data-dismiss-message @click="dismissMessage(m.id)">✕</button>
          </header>
          <p class="msg-body" :class="{ clamped: !isExpanded(m.id) }" data-message-body>{{ m.body }}</p>
          <footer class="msg-tile-foot">
            <span v-if="m.priority" class="msg-delivery" data-message-delivery>{{ deliveryLabel(m) }}</span>
            <button v-if="needsClamp(m)" class="ghost msg-expand" data-message-expand :aria-expanded="isExpanded(m.id) ? 'true' : 'false'"
                    @click="toggleExpanded(m.id)">{{ isExpanded(m.id) ? 'Show less' : 'Show more' }}</button>
          </footer>
        </article>
      </div>
      <div v-else class="messages-empty" data-messages-empty>
        No open messages. Fleet peers reach this session with agent_send; priority ones go straight into its prompt.
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
    <SecretRequestBanner v-if="capabilities.canManage && !sharedToken" :session-id="session.id" />
    <SessionWorkspace ref="workspace" :session="session" :can-write="capabilities.canWrite" :shared-token="sharedToken">
      <div class="terminal-stack">
        <ConversationTimeline v-if="modern && !isChat" :key="session.id" :items="transcriptItems" :loading="transcriptBusy"
          :error="transcriptError" :source="conversation?.source" :can-quote="canCompose" :inviting="canTask"
          @retry="loadTranscript" @quote="composer?.quote($event)" />
        <WorkspaceStream v-if="isChat" v-show="modern" :session="session" :shared-token="sharedToken" :readonly="!capabilities.canWrite" @status="statuses.agent = $event" />
        <CliMenuNotice v-if="modern && !isChat && cliMenu" :menu="cliMenu" :can-answer="capabilities.canWrite && isLive"
          @choose="answerCliMenu" @cancel="cancelCliMenu" />
        <div v-if="modern" class="drawer-toolbar">
          <span v-if="!isChat" class="sync-note">Conversation · follows the agent live</span>
          <button v-if="!isChat" type="button" :aria-expanded="terminalOpened && drawerKind === 'agent'" data-drawer-agent @click="toggleDrawer('agent')">⌘ Agent terminal</button>
          <button v-if="isLive && capabilities.canShell" type="button" :aria-expanded="terminalOpened && drawerKind === 'shell'" data-drawer-shell @click="toggleDrawer('shell')">›_ Shell</button>
        </div>
        <div v-if="!isChat || (isLive && capabilities.canShell && shellOpened)" class="terminal-host"
          :class="{ 'terminal-drawer': modern, 'menu-open': modern && cliMenu && drawerKind === 'agent' }" v-show="activeTab === 'agent' || activeTab === 'shell' || (modern && terminalOpened)">
          <TerminalPane v-if="!isChat" ref="agentTerminal" v-show="activeTab === 'agent' || (modern && drawerKind === 'agent')"
            :session="session" :shared-token="sharedToken" :readonly="!capabilities.canWrite" kind="agent" :show-composer="!modern"
            :active="activeTab === 'agent' || (modern && terminalOpened && drawerKind === 'agent')" @status="statuses.agent = $event"
            @menu="onCliMenu" />
          <TerminalPane v-if="isLive && capabilities.canShell && shellOpened" v-show="activeTab === 'shell' || (modern && drawerKind === 'shell')"
            :session="session" kind="shell" :active="activeTab === 'shell' || (modern && terminalOpened && drawerKind === 'shell')" @status="statuses.shell = $event" />
        </div>
        <WorkspaceComposer v-if="workspaceOpened && !isChat && capabilities.canWrite && String(session.mode).toLowerCase() === 'interactive'" v-show="modern"
          ref="composer" :session-id="session.id" :enabled="canCompose" :queueable="canTask && !canCompose" :status="statuses.agent" :agent="session.agent"
          :send="sendWorkspaceMessage" :interrupt="interruptWorkspaceAgent" />
      </div>
    </SessionWorkspace>
  </div>
</template>
<style scoped>
.term-wrap { flex: 1; display: flex; flex-direction: column; min-width: 0; min-height: 0; background: #0e0d0b; }
.term-bar { display: flex; align-items: center; gap: 10px; padding: 10px 16px; border-bottom: 1px solid var(--border); background: var(--bg); position: relative; flex-wrap: wrap; }
.back { width: 30px; height: 30px; display: flex; align-items: center; justify-content: center; border-radius: 9px; border: none; background: none; color: var(--muted); font-size: 16px; padding: 0; flex-shrink: 0; }
.back:hover { background: var(--panel-2); color: var(--text); }
/* A floor for the title: with account, Files, four tabs and the actions in one bar, a zero
   min-width let the flex row squeeze the session name to its first letter; the controls wrap
   onto the next line instead. */
.head-main { min-width: min(240px, 100%); flex: 1 1 240px; }
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
.msg-badge { display: inline-block; min-width: 18px; margin-left: 6px; padding: 1px 6px; border-radius: 999px; background: var(--accent); color: var(--on-accent); font-size: 11px; font-weight: 700; line-height: 16px; text-align: center; }
.messages-panel { display: flex; flex-direction: column; border-bottom: 1px solid var(--border-2); background: var(--bg); max-height: 48vh; overflow-y: auto; }
.messages-head { display: flex; align-items: center; gap: 10px; padding: 8px 20px; font-size: 12px; color: var(--strong); border-bottom: 1px solid var(--border-2); background: var(--panel); position: sticky; top: 0; z-index: 1; }
.messages-head .ghost { margin-left: auto; }
.messages-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(320px, 1fr)); gap: 10px; padding: 12px 20px 14px; }
.msg-tile { display: flex; flex-direction: column; gap: 6px; min-width: 0; padding: 10px 12px 10px 14px; background: var(--panel); border: 1px solid var(--border-2); border-left: 3px solid var(--accent-2); border-radius: 12px; font-size: 12px; }
.msg-tile.priority { border-left-color: var(--accent); background: #0f1d2e; }
.msg-tile.interrupt { border-left-color: var(--warn); }
.msg-tile-head { display: flex; align-items: baseline; gap: 6px; min-width: 0; }
.msg-kind { font: 700 10px/1 var(--display); letter-spacing: .08em; text-transform: uppercase; color: var(--accent-2); white-space: nowrap; }
.msg-kind.priority { color: var(--accent); }
.msg-tile.interrupt .msg-kind { color: var(--warn); }
.msg-from { color: var(--strong); font-weight: 600; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.msg-time { margin-left: auto; color: var(--muted-2); font-size: 11px; white-space: nowrap; }
.msg-dismiss { padding: 0 2px; line-height: 1; color: var(--muted-2); }
.msg-dismiss:hover { color: var(--strong); }
.msg-tile .msg-body { margin: 0; color: var(--text); font-size: 13px; line-height: 1.5; white-space: pre-wrap; overflow-wrap: anywhere; }
.msg-tile .msg-body.clamped { display: -webkit-box; -webkit-line-clamp: 3; -webkit-box-orient: vertical; overflow: hidden; }
.msg-tile-foot { display: flex; align-items: center; gap: 10px; min-height: 16px; }
.msg-tile .msg-delivery { color: var(--muted-2); font-size: 11px; }
.msg-expand { margin-left: auto; padding: 0; font-size: 11px; color: var(--accent); }
.messages-empty { padding: 14px 20px; color: var(--muted); font-size: 12px; }
.msg-body { color: var(--muted); white-space: pre-wrap; overflow-wrap: anywhere; }
.terminal-stack { flex: 1; display: flex; flex-direction: column; min-width: 0; min-height: 0; }
.terminal-host { flex: 1; display: flex; min-width: 0; min-height: 0; }
.terminal-host.terminal-drawer { flex: 0 0 32%; min-height: 140px; border-block: 1px solid var(--border); }
/* A CLI fits its dialog into the rows it has, scrolling its list down to the highlighted entry;
   the drawer grows while one is open so the whole menu is drawn, for the buttons and the person. */
.terminal-host.terminal-drawer.menu-open { flex-basis: 50%; min-height: 300px; }
.modern-workspace { background: var(--bg); }
.drawer-toolbar { display: flex; align-items: center; justify-content: flex-end; gap: 6px; padding: 6px 16px; border-top: 1px solid var(--border); background: var(--bg); }
.drawer-toolbar button { border: 0; background: transparent; color: var(--muted-2); font-size: 11px; padding: 5px 8px; }
.drawer-toolbar button[aria-expanded=true] { color: var(--strong); background: var(--panel-2); }
.sync-note { margin-right: auto; color: var(--muted-3); font-size: 10px; }
@media(max-width: 640px) { .sync-note { max-width: 45%; line-height: 1.4; } .perm-actions { flex-wrap: wrap; } }
</style>
