<script setup>
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import {
  api,
  getSharedFileCapabilities,
  getSharedFilePresentation,
  listSharedSessionFiles,
  sessionEventsUrl,
  sharedSessionEventsUrl
} from '../api.js'
import { openSessionEvents } from '../lib/live.js'
import BrowserPane from './BrowserPane.vue'
import FilesPane from './FilesPane.vue'
import WorkspaceDiffPane from './workspace/WorkspaceDiffPane.vue'
import SessionStartupFailure from './SessionStartupFailure.vue'

const props = defineProps({
  session: { type: Object, required: true },
  canWrite: { type: Boolean, default: false },
  sharedToken: { type: String, default: null }
})
const splitPercent = ref(50)
const mobilePane = ref((props.session?.browser?.phase || 'Stopped') !== 'Stopped' ? 'browser' : 'agent')
const browserRunning = computed(() => (props.session?.browser?.phase || 'Stopped') !== 'Stopped')
// Hiding is local to this view and leaves the lease alone: stopping the browser would end the
// agent's page mid-task, and the agent is the one that asked for it. The flag lives here rather
// than in TerminalView's tabs because this component wraps both surfaces, so switching between
// Workspace and Terminal keeps the person's choice instead of popping the pane back open.
const browserHidden = ref(false)
const browserVisible = computed(() => browserRunning.value && !browserHidden.value)
let dragging = false
const activeCompanion = ref(browserVisible.value ? 'browser' : 'files')
// Changes is a view of the Files pane, not a companion of its own (#180): the session bar has one
// entry point for the workspace's files and what changed in them. It stays opt-in per viewer —
// the diff pane mounts, and polls, only while this viewer has Changes picked in an open Files pane.
const filesView = ref('files')
const filesOpen = ref(false)
const files = ref([])
const selectedId = ref(null)
const fileCapabilities = ref({})
const presentation = ref(null)
let presentationRevision = -1
let presentationTimer
let loadGeneration = 0
let eventStream
// The poll is the fallback now, not the mechanism. While the socket is up it only has to catch
// an event the backend never managed to send — a replica restarting mid-notification, say — so
// it runs two orders of magnitude slower than it used to.
const LIVE_FALLBACK_POLL_MS = 30_000
const liveConnected = ref(false)
const selectedFile = computed(() => files.value.find(file => file.id === selectedId.value) || null)
const filesVisible = computed(() => filesOpen.value || Boolean(selectedFile.value))
const diffOpen = computed(() => filesVisible.value && filesView.value === 'changes')
const companionVisible = computed(() => browserVisible.value || filesVisible.value)
const isPresented = computed(() => Boolean(selectedId.value && presentation.value?.fileId === selectedId.value))

function capabilitiesRequest() {
  return props.sharedToken ? getSharedFileCapabilities(props.sharedToken) : api.sessionFileCapabilities(props.session.id)
}
function listRequest() {
  return props.sharedToken ? listSharedSessionFiles(props.sharedToken) : api.listSessionFiles(props.session.id)
}
function presentationRequest() {
  return props.sharedToken ? getSharedFilePresentation(props.sharedToken) : api.getFilePresentation(props.session.id)
}
async function refreshFiles(generation = loadGeneration) {
  try {
    const listed = await listRequest()
    if (generation === loadGeneration) files.value = Array.isArray(listed) ? listed : []
  } catch {
    // Keep the last listing. Emptying it dropped the selected file, so the preview fell back
    // to "Select a file" and the next successful read handed the same file back as a new
    // selection — refetched and reframed. Every refresh that failed (a backend rollout, a proxy
    // hiccup, a reconnect storm on the event socket) became a visible preview reload. A
    // session switch clears the list on its own, so stale entries cannot cross sessions, and a
    // revoked share is handled where the session itself is fetched.
  }
}
async function refreshPresentation(generation = loadGeneration) {
  try {
    const previouslyPresented = presentation.value?.fileId
    const next = await presentationRequest()
    if (generation !== loadGeneration || !next || !Number.isFinite(next.revision) || next.revision <= presentationRevision) return
    presentationRevision = next.revision
    presentation.value = next
    if (next.fileId) {
      if (!files.value.some(file => file.id === next.fileId)) await refreshFiles(generation)
      if (generation !== loadGeneration) return
      selectedId.value = next.fileId
      filesOpen.value = true
      filesView.value = 'files'
      activeCompanion.value = 'files'
      mobilePane.value = 'files'
    } else if (previouslyPresented && selectedId.value === previouslyPresented) {
      closeFiles()
    }
  } catch { /* presentation is optional */ }
}
function pollDelay() {
  if (liveConnected.value) return LIVE_FALLBACK_POLL_MS
  const configured = Number(fileCapabilities.value?.limits?.presentationPollMilliseconds)
  return Math.max(500, configured || 1500)
}
function schedulePresentationPoll(generation) {
  clearTimeout(presentationTimer)
  presentationTimer = setTimeout(async () => {
    if (generation !== loadGeneration) return
    if (filesOpen.value) await refreshFiles(generation)
    await refreshPresentation(generation)
    if (generation === loadGeneration) schedulePresentationPoll(generation)
  }, pollDelay())
}
async function onSessionEvent(generation) {
  if (generation !== loadGeneration) return
  if (filesOpen.value) await refreshFiles(generation)
  await refreshPresentation(generation)
}
function openEventStream(generation) {
  eventStream?.close()
  eventStream = openSessionEvents(
    () => props.sharedToken ? sharedSessionEventsUrl(props.sharedToken) : sessionEventsUrl(props.session.id),
    () => { void onSessionEvent(generation) },
    connected => {
      if (generation !== loadGeneration) return
      liveConnected.value = connected
      // The poll's cadence depends on this flag, and a timer already waiting out the slow
      // fallback would keep the pane stale for half a minute after the socket dropped.
      schedulePresentationPoll(generation)
    })
}
async function initializeFiles() {
  const generation = ++loadGeneration
  clearTimeout(presentationTimer)
  eventStream?.close()
  eventStream = undefined
  liveConnected.value = false
  try { fileCapabilities.value = await capabilitiesRequest() } catch { fileCapabilities.value = {} }
  if (generation !== loadGeneration) return
  await Promise.all([refreshFiles(generation), refreshPresentation(generation)])
  if (generation !== loadGeneration) return
  openEventStream(generation)
  schedulePresentationPoll(generation)
}
function selectFile(id) {
  selectedId.value = id; filesOpen.value = true; filesView.value = 'files'; activeCompanion.value = 'files'; mobilePane.value = 'files'
}
function closeFiles() {
  filesOpen.value = false
  selectedId.value = null
  filesView.value = 'files'
  if (browserVisible.value) { activeCompanion.value = 'browser'; mobilePane.value = 'browser' } else mobilePane.value = 'agent'
}
async function dismissPresentation() {
  if (!props.canWrite || props.sharedToken) return
  try {
    const next = await api.setFilePresentation(props.session.id, null)
    if (next) { presentation.value = next; presentationRevision = Math.max(presentationRevision, next.revision || 0) }
    closeFiles()
  } catch { /* keep the current presentation visible */ }
}
function closeBrowser() {
  browserHidden.value = true
  if (filesVisible.value) { activeCompanion.value = 'files'; mobilePane.value = 'files' } else mobilePane.value = 'agent'
}
function openBrowser() {
  if (!browserRunning.value) return
  browserHidden.value = false
  activeCompanion.value = 'browser'; mobilePane.value = 'browser'
}
function closeCompanion(which) {
  if (which === 'browser') closeBrowser()
  else closeFiles()
}
// Escape closes the pane only from its own chrome — tabs, file list, preview toolbar. While the
// remote browser's canvas has focus, keys are forwarded to the page the agent is driving, and
// taking Escape there would make it impossible to dismiss a dialog inside that page.
function onCompanionKey(event) {
  if (event.key !== 'Escape' || event.defaultPrevented) return
  if (event.target?.closest?.('[data-browser-canvas]')) return
  event.preventDefault()
  closeCompanion(activeCompanion.value)
}
async function openFiles() {
  await refreshFiles()
  filesOpen.value = true
  if (!selectedId.value && files.value.length) selectedId.value = files.value[0].id
  activeCompanion.value = 'files'; mobilePane.value = 'files'
}
defineExpose({ openFiles, openBrowser })

function clamp(value) { return Math.min(75, Math.max(25, value)) }
function resizeBy(delta) { splitPercent.value = clamp(splitPercent.value + delta) }
function onSeparatorKey(event) {
  if (event.key === 'ArrowLeft') { event.preventDefault(); resizeBy(-5) }
  if (event.key === 'ArrowRight') { event.preventDefault(); resizeBy(5) }
  if (event.key === 'Home') { event.preventDefault(); splitPercent.value = 25 }
  if (event.key === 'End') { event.preventDefault(); splitPercent.value = 75 }
}
function onPointerMove(event) {
  if (!dragging) return
  const workspace = event.currentTarget?.document?.querySelector?.('[data-session-split]') || document.querySelector('[data-session-split]')
  const bounds = workspace?.getBoundingClientRect()
  if (bounds?.width) splitPercent.value = clamp(((event.clientX - bounds.left) / bounds.width) * 100)
}
function stopDragging() {
  dragging = false
  window.removeEventListener('pointermove', onPointerMove)
  window.removeEventListener('pointerup', stopDragging)
}
function startDragging(event) {
  event.preventDefault()
  dragging = true
  window.addEventListener('pointermove', onPointerMove)
  window.addEventListener('pointerup', stopDragging)
}
onMounted(initializeFiles)
watch(() => props.session.id, () => {
  filesView.value = 'files'; files.value = []; selectedId.value = null; filesOpen.value = false; browserHidden.value = false
  presentation.value = null; presentationRevision = -1; void initializeFiles()
})
// A browser that stops and is requested again is a new lease the agent wants seen, so the
// person's earlier "hide" does not carry over to it.
watch(browserRunning, running => { if (!running) browserHidden.value = false })
watch(browserVisible, visible => {
  if (visible && !filesOpen.value && !selectedFile.value) { activeCompanion.value = 'browser'; mobilePane.value = 'browser' }
  else if (!visible && activeCompanion.value === 'browser') activeCompanion.value = 'files'
})
onBeforeUnmount(() => {
  stopDragging()
  loadGeneration += 1
  clearTimeout(presentationTimer)
  eventStream?.close()
  eventStream = undefined
})
</script>

<template>
  <SessionStartupFailure v-if="session?.failureReason" :reason="session.failureReason" />
  <div v-if="companionVisible" class="workspace-shell">
    <nav class="workspace-tabs" aria-label="Session view">
      <button v-if="filesVisible" data-mobile-workspace-tab="files" :class="{ active: mobilePane === 'files' }" @click="mobilePane = 'files'; activeCompanion = 'files'">Files</button>
      <button v-if="browserVisible" data-mobile-workspace-tab="browser" :class="{ active: mobilePane === 'browser' }" @click="mobilePane = 'browser'; activeCompanion = 'browser'">Browser</button>
      <button data-mobile-workspace-tab="agent" :class="{ active: mobilePane === 'agent' }" @click="mobilePane = 'agent'">Agent</button>
    </nav>
    <div data-session-split class="session-split" :style="{ '--browser-width': `${splitPercent}%` }">
      <div class="companion-side" :class="{ 'mobile-hidden': mobilePane === 'agent' }" @keydown="onCompanionKey">
        <!-- Each pane closes from its own tab, which is the one row present for both panes at every
             width; the phone tab strip above only switches between panes and the agent. -->
        <nav class="companion-tabs" aria-label="Companion view">
          <span v-if="filesVisible" class="companion-tab" :class="{ active: activeCompanion === 'files' }">
            <button data-workspace-tab="files" @click="activeCompanion = 'files'">Files</button>
            <button type="button" class="tab-close" data-files-close aria-label="Close files" title="Close files (Esc)" @click="closeFiles">×</button>
          </span>
          <span v-if="browserVisible" class="companion-tab" :class="{ active: activeCompanion === 'browser' }">
            <button data-workspace-tab="browser" @click="activeCompanion = 'browser'">Browser</button>
            <button type="button" class="tab-close" data-browser-close aria-label="Close browser" title="Close browser (Esc) — the agent keeps it" @click="closeBrowser">×</button>
          </span>
        </nav>
        <div class="companion-body">
          <div v-show="activeCompanion === 'files'" class="files-companion">
            <div class="files-views" role="tablist" aria-label="Files view">
              <button type="button" role="tab" data-files-view="files" :aria-selected="filesView === 'files'" @click="filesView = 'files'">Files</button>
              <button type="button" role="tab" data-files-view="changes" :aria-selected="filesView === 'changes'" @click="filesView = 'changes'">Changes</button>
            </div>
            <WorkspaceDiffPane v-if="diffOpen" :session-id="session.id" :shared-token="sharedToken" :active="activeCompanion === 'files' && mobilePane !== 'agent'" />
            <FilesPane v-show="filesView === 'files'" :session-id="session.id" :files="files" :selected-id="selectedId"
            :capabilities="fileCapabilities" :can-write="canWrite && !sharedToken" :shared-token="sharedToken"
            :presented="isPresented" @select="selectFile" @dismiss="dismissPresentation"
            @uploaded="refreshFiles()" />
          </div>
          <BrowserPane v-if="browserVisible" v-show="activeCompanion === 'browser'" :session="session" :can-write="canWrite" :shared-token="sharedToken" />
        </div>
      </div>
      <div class="split-seam" role="separator" tabindex="0" aria-label="Companion width" aria-orientation="vertical" aria-valuemin="25" aria-valuemax="75" :aria-valuenow="Math.round(splitPercent)" @keydown="onSeparatorKey" @pointerdown="startDragging"><span></span></div>
      <div class="terminal-side" :class="{ 'mobile-hidden': mobilePane !== 'agent' }"><slot /></div>
    </div>
  </div>
  <div v-else class="terminal-only"><slot /></div>
</template>

<style scoped>
.workspace-shell, .terminal-only { flex: 1; display: flex; flex-direction: column; min-width: 0; min-height: 0; }
.session-split { flex: 1; display: flex; min-width: 0; min-height: 0; }
.companion-side { flex: 0 0 var(--browser-width); display: grid; grid-template-rows: 34px minmax(0, 1fr); min-width: 0; min-height: 0; background: #111721; }
.companion-tabs { display: flex; align-items: end; gap: 2px; padding: 4px 8px 0; border-bottom: 1px solid #29303a; background: #171c24; }
.companion-tab { display: inline-flex; align-items: center; height: 29px; border-radius: 6px 6px 0 0; }
.companion-tab.active { background: #222b37; box-shadow: inset 0 -2px var(--accent); }
.companion-tab button { height: 29px; padding: 4px 6px 4px 10px; border: 0; color: #738094; background: transparent; font: 700 10px var(--mono); text-transform: uppercase; }
.companion-tab.active button { color: #eef5ff; }
.companion-tab .tab-close { min-width: 26px; padding: 0 8px 0 4px; font: 15px/1 var(--ui); text-transform: none; }
.companion-tab .tab-close:hover, .companion-tab .tab-close:focus-visible { color: var(--accent); }
.companion-body { min-width: 0; min-height: 0; }
.files-companion { display: grid; grid-template-rows: auto minmax(0, 1fr); height: 100%; min-height: 0; }
.files-views { display: flex; gap: 2px; padding: 4px 8px; border-bottom: 1px solid #29303a; background: #111721; }
.files-views button { padding: 3px 10px; border: 0; border-radius: 6px; color: #738094; background: transparent; font-size: 12px; font-weight: 600; }
.files-views button:hover { color: #eef5ff; background: #1b222c; }
.files-views button[aria-selected="true"] { color: #eef5ff; background: #222b37; }
.terminal-side { flex: 1 1 auto; display: flex; min-width: 0; min-height: 0; }
.split-seam { position: relative; flex: 0 0 9px; cursor: col-resize; background: #151719; border-inline: 1px solid #292a29; outline: none; touch-action: none; }
.split-seam span { position: absolute; inset: 50% 2px auto; height: 42px; transform: translateY(-50%); border-radius: 6px; background: #353a40; transition: background .15s, box-shadow .15s; }
.split-seam:hover span, .split-seam:focus-visible span { background: var(--accent); box-shadow: 0 0 0 3px rgba(90,169,245,.13); }
.workspace-tabs { display: none; height: 38px; flex: 0 0 38px; align-items: center; gap: 2px; padding: 4px; background: var(--bg); border-bottom: 1px solid var(--border); }
.workspace-tabs button { flex: 1; padding: 5px 10px; border: 0; border-radius: 7px; color: var(--muted-3); background: transparent; font-size: 12px; }
.workspace-tabs button.active { color: var(--strong); background: var(--panel-2); }
@media (max-width: 760px) {
  .workspace-tabs { display: flex; }
  .session-split { display: block; }
  .companion-side, .terminal-side { width: 100%; height: 100%; }
  .split-seam { display: none; }
  /* A finger needs more than the 26px a pointer gets. */
  .companion-tab .tab-close { min-width: 40px; }
  .mobile-hidden { display: none; }
}
</style>
