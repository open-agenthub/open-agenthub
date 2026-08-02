<script setup>
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import TerminalPane from './TerminalPane.vue'
import SessionWorkspace from './SessionWorkspace.vue'
import ChatPane from './ChatPane.vue'
import ShareSessionDialog from './ShareSessionDialog.vue'
import { canPause, sessionStatus, statusStyle, tabLabel } from '../lib/status.js'
import { sessionCapabilities } from '../lib/access.js'
import { api, getSharedTranscript } from '../api.js'
import { repoShortName } from '../lib/text.js'
import { authLabel } from '../lib/agent.js'
import { toTranscriptBlocks } from '../lib/transcript.js'

const props = defineProps({ session: Object, sharedToken: { type: String, default: null } })
defineEmits(['back', 'resume', 'pause', 'edit', 'duplicate'])
const capabilities = computed(() => sessionCapabilities(props.session))
const isLive = computed(() => ['Running', 'Pending'].includes(props.session?.phase))
// Chat sessions render the structured stream; their agent pane already replays
// history, so the raw Transcript tab stays terminal-only.
const isChat = computed(() => props.session?.uiMode === 'chat')
const activeTab = ref('agent')
watch(isChat, chat => { if (chat && activeTab.value === 'transcript') activeTab.value = 'agent' })
const shellOpened = ref(false)
const shareOpen = ref(false)
const transcriptText = ref(null)
const transcriptBlocks = computed(() => toTranscriptBlocks(transcriptText.value))
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

async function decidePermission(reqId, decision) {
  pendingPermissions.value = pendingPermissions.value.filter(p => p.id !== reqId)
  try { await api.decidePermission(props.session.id, reqId, decision) } catch {}
}

onMounted(() => {
  refreshPermissions()
  permissionTimer = setInterval(refreshPermissions, 4000)
})
onBeforeUnmount(() => clearInterval(permissionTimer))
watch(() => props.session?.id, () => { pendingPermissions.value = []; refreshPermissions() })

const repoLabel = computed(() => repoShortName(props.session?.repoUrl || props.session?.repos?.[0]?.url || ''))

async function selectTab(tab) {
  if (tab === 'shell') shellOpened.value = true
  activeTab.value = tab
  if (tab === 'transcript' && transcriptText.value === null) {
    try {
      transcriptText.value = props.sharedToken
        ? await getSharedTranscript(props.sharedToken)
        : await api.getTranscript(props.session.id)
    } catch { transcriptText.value = '' }
  }
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
          <span v-if="session.agent">· {{ session.agent }}<template v-if="session.authMode"> / {{ authLabel(session.authMode) }}</template></span>
          <span v-if="repoLabel" class="mono">· {{ repoLabel }}</span>
          <span v-if="session.schedule" class="cron">· ▶ {{ session.schedule }}</span>
          <span v-if="session.sharedBy" class="shared">· shared by {{ session.sharedBy }}</span>
        </div>
      </div>
      <nav class="tabs">
        <button :class="{ on: activeTab === 'agent' }" @click="selectTab('agent')">{{ tabLabel('agent') }}</button>
        <button v-if="isLive && capabilities.canShell" :class="{ on: activeTab === 'shell' }" @click="selectTab('shell')">{{ tabLabel('shell') }}</button>
        <button v-if="!isChat" :class="{ on: activeTab === 'transcript' }" @click="selectTab('transcript')">Transcript</button>
      </nav>
      <span v-if="!capabilities.canWrite" class="readonly">Read-only</span>
      <template v-if="capabilities.canManage">
        <button v-if="canPause(session)" class="bar-btn" @click="$emit('pause', session.id)">❚❚ Pause</button>
        <button v-if="session.canResume" class="bar-btn" @click="$emit('resume', session.id)">▶ Resume</button>
        <button class="bar-btn" @click="$emit('edit', session.id)">✎ Edit session</button>
        <button class="bar-btn primary" @click="shareOpen = !shareOpen">↗ Share</button>
      </template>
      <span class="status">{{ statuses[activeTab] }}</span>
      <div v-if="shareOpen" class="share-pop">
        <div class="share-head"><span>Share session</span><button class="ghost" @click="shareOpen = false">✕</button></div>
        <ShareSessionDialog embedded :session="session" @close="shareOpen = false" />
      </div>
    </div>
    <div v-if="session.questionPending && capabilities.canWrite" class="asking">
      <span class="ask-dot"></span>THE AGENT IS ASKING — reply {{ isChat ? 'below' : 'in the terminal below' }}.
    </div>
    <div v-for="p in pendingPermissions" :key="p.id" class="perm">
      <span class="ask-dot"></span>
      <div class="perm-text">
        <strong>The agent wants to use {{ p.tool }}.</strong>
        <span v-if="p.summary" class="perm-summary">{{ p.summary }}</span>
      </div>
      <div class="perm-actions">
        <button class="bar-btn primary" @click="decidePermission(p.id, 'allow')">Allow</button>
        <button class="bar-btn" @click="decidePermission(p.id, 'allowAlways')">Allow (don't ask again)</button>
        <button class="bar-btn danger" @click="decidePermission(p.id, 'deny')">Deny</button>
      </div>
    </div>
    <SessionWorkspace :session="session" :can-write="capabilities.canWrite" :shared-token="sharedToken">
      <div class="terminal-stack">
        <ChatPane v-if="isChat" v-show="activeTab === 'agent'" :session="session" :shared-token="sharedToken" :readonly="!capabilities.canWrite" :active="activeTab === 'agent'" @status="statuses.agent = $event" />
        <TerminalPane v-else v-show="activeTab === 'agent'" :session="session" :shared-token="sharedToken" :readonly="!capabilities.canWrite" kind="agent" :active="activeTab === 'agent'" @status="statuses.agent = $event" />
        <TerminalPane v-if="isLive && capabilities.canShell && shellOpened" v-show="activeTab === 'shell'" :session="session" kind="shell" :active="activeTab === 'shell'" @status="statuses.shell = $event" />
        <section v-if="activeTab === 'transcript'" class="transcript" aria-labelledby="transcript-heading">
          <div class="transcript-inner">
            <h3 id="transcript-heading">What happened so far</h3>
            <p v-if="transcriptText === null" class="transcript-state">Loading…</p>
            <p v-else-if="!transcriptBlocks.length" class="transcript-state">[no saved transcript]</p>
            <ol v-else class="transcript-list" aria-label="Terminal transcript">
              <li v-for="(block, index) in transcriptBlocks" :key="index" class="transcript-bubble">
                <span class="transcript-label">Terminal</span>
                <pre>{{ block }}</pre>
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
.shared { color: var(--accent); }
.tabs { display: flex; gap: 2px; background: var(--panel); border: 1px solid var(--border-2); border-radius: var(--radius); padding: 3px; }
.tabs button { font-size: 12px; font-weight: 700; padding: 5px 14px; border-radius: 8px; border: none; background: none; color: var(--muted-3); }
.tabs button:hover { color: var(--text); background: none; }
.tabs button.on { background: var(--border-2); color: var(--strong); }
.bar-btn { font-size: 12px; padding: 6px 14px; border-radius: 9px; white-space: nowrap; }
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
.terminal-stack { flex: 1; display: flex; flex-direction: column; min-width: 0; min-height: 0; }
.transcript { flex: 1; overflow-y: auto; min-height: 0; background: var(--bg); }
.transcript-inner { max-width: 760px; margin: 0 auto; padding: 26px 24px; }
.transcript-inner h3 { font-size: 20px; margin: 0 0 14px; }
.transcript-list { display: flex; flex-direction: column; gap: 12px; list-style: none; margin: 0; padding: 0; }
.transcript-bubble { padding: 12px 14px 14px; background: var(--panel); border: 1px solid var(--border-2); border-left: 3px solid var(--accent); border-radius: 12px; }
.transcript-label { display: block; color: var(--muted-3); font: 700 10px/1 var(--display); letter-spacing: .08em; text-transform: uppercase; }
.transcript-bubble pre { margin: 7px 0 0; white-space: pre-wrap; overflow-wrap: anywhere; font: 13px/1.6 var(--mono); color: #c9c4bb; }
.transcript-state { margin: 0; color: var(--muted-3); font: 13px/1.6 var(--mono); }
</style>
