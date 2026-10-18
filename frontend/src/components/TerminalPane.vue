<script setup>
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { Terminal } from '@xterm/xterm'
import { FitAddon } from '@xterm/addon-fit'
import { api, getSharedTranscript, sharedTerminalUrl, shellUrl, terminalUrl } from '../api.js'
import { SUBMIT_ENTER_DELAY_MS, submitToAgent } from '../lib/terminal-input.js'
import { FILE_PASTE_NOTICE, attachTerminalClipboard } from '../lib/terminal-clipboard.js'
import { terminalAttachmentPaths, terminalSafeFile, uploadAttachmentFiles } from '../lib/attachments.js'
import { detectMenu, sameMenu } from '../lib/cli-menu.js'

const props = defineProps({ session: Object, kind: { type: String, default: 'agent' }, active: { type: Boolean, default: true }, readonly: { type: Boolean, default: false }, sharedToken: { type: String, default: null }, showComposer: { type: Boolean, default: true } })
const emit = defineEmits(['status', 'menu'])
const host = ref(null)
const mobileInput = ref('')
const pasteNotice = ref('')
let pasteNoticeTimer
let term, fit, clipboard, ws, ro, reconnectTimer
let disposed = false
let inputRegistered = false
let connectionGeneration = 0
let fitFrame = 0
let lastSentCols = 0
let lastSentRows = 0
let cancelSubmission

const isLive = computed(() => props.kind === 'shell' || ['Running', 'Pending'].includes(props.session?.phase))
const canSend = computed(() => isLive.value && !props.readonly)

function send(value) {
  if (canSend.value && ws?.readyState === WebSocket.OPEN) ws.send(JSON.stringify(value))
}

// Each attachment path is a bracketed paste of its own: the CLIs recognise a pasted image path
// only when it is the whole paste. The gap lets the CLI turn one into "[Image #n]" before the next
// paste arrives.
const PATH_PASTE_GAP_MS = 150
// Bracketed paste preserves multiline prompts as one edit instead of executing each line.
// Strip terminal controls from pasted text so it cannot close its own paste envelope.
function bracketed(text) {
  return '\x1b[200~' + String(text).replace(/\r\n?/g, '\n').replace(/[\x00-\x08\x0b-\x1f\x7f]/g, '') + '\x1b[201~'
}

// Writes the pastes one after another on this pane's connection, then Enter when `submit` is
// set. A connection that changes in between ends the sequence: what was already written stays in
// the CLI's input box, and the error says to look there.
function writePastes(pieces, submit) {
  const socket = ws
  const generation = connectionGeneration
  // retryable: nothing was written, so the composer may send the same text again later.
  if (!canSend.value || socket?.readyState !== WebSocket.OPEN || cancelSubmission)
    return Promise.reject(Object.assign(new Error('Agent is disconnected. Your draft is still here.'), { retryable: true }))
  return new Promise((resolve, reject) => {
    let timer
    const finish = error => {
      clearTimeout(timer)
      cancelSubmission = undefined
      if (error) reject(error)
      else resolve()
    }
    cancelSubmission = () => finish(new Error('Connection changed. Check the agent terminal before retrying your draft.'))
    const step = index => {
      if (index > 0 && (generation !== connectionGeneration || socket !== ws || !canSend.value || socket.readyState !== WebSocket.OPEN))
        return cancelSubmission?.()
      try {
        if (index === pieces.length) {
          if (submit) socket.send(JSON.stringify({ type: 'input', data: '\r' }))
          return finish()
        }
        socket.send(JSON.stringify({ type: 'input', data: bracketed(pieces[index]) }))
        const last = index === pieces.length - 1
        if (last && !submit) return finish()
        timer = setTimeout(() => step(index + 1), last ? SUBMIT_ENTER_DELAY_MS : PATH_PASTE_GAP_MS)
      } catch (error) { finish(error) }
    }
    step(0)
  })
}

// Reuse this pane's connection: a second terminal client could resize the same PTY
// and two independently reconnecting clients could submit to different generations.
// `paths` are attachments the session agent materialized (#148); they go first, so the text
// follows the CLI's "[Image #n]" markers in its input box.
function submitMessage(text, { paths = [] } = {}) {
  const value = String(text)
  if (!value.trim() && !paths.length) return Promise.reject(new Error('Enter a message.'))
  const pieces = [...paths, ...(value.trim() ? [paths.length ? ' ' + value : value] : [])]
  return writePastes(pieces, true)
}

// An image pasted into the agent terminal: uploaded, materialized by the session agent, and its
// path pasted into the CLI's input box without Enter — the person adds their words and sends it,
// as after pasting an image into the CLI on their own machine.
const canPasteFiles = computed(() => props.kind === 'agent' && canSend.value && !props.sharedToken)
async function pasteFilesIntoAgent(files) {
  if (!canPasteFiles.value) return showPasteNotice(FILE_PASTE_NOTICE)
  const sessionId = props.session.id
  const now = new Date()
  showPasteNotice(files.length === 1 ? 'Attaching the pasted file…' : `Attaching ${files.length} pasted files…`)
  try {
    const ids = await uploadAttachmentFiles({ sessionId, api, files: files.map((file, index) => terminalSafeFile(file, { pasted: true, now, index })) })
    const paths = await terminalAttachmentPaths(api, sessionId, ids)
    if (disposed || sessionId !== props.session.id) return
    await writePastes(paths, false)
    showPasteNotice('Attached to the agent\'s input. Add your message and press Enter.')
  } catch (error) {
    if (!disposed) showPasteNotice(error?.message || 'The pasted file could not be attached.')
  }
}

function interruptAgent() {
  if (!canSend.value || ws?.readyState !== WebSocket.OPEN) throw new Error('Agent is disconnected')
  cancelSubmission?.()
  ws.send(JSON.stringify({ type: 'input', data: '\x03' }))
}
defineExpose({ submitMessage, interruptAgent, sendKeys })

function showPasteNotice(text) {
  pasteNotice.value = text
  clearTimeout(pasteNoticeTimer)
  pasteNoticeTimer = setTimeout(() => { pasteNotice.value = '' }, 5000)
}

function sendComposerMessage() {
  if (submitToAgent(send, mobileInput.value)) mobileInput.value = ''
}

function resize(force = false) {
  if (!term) return
  if (!force && term.cols === lastSentCols && term.rows === lastSentRows) return
  lastSentCols = term.cols
  lastSentRows = term.rows
  send({ type: 'resize', cols: term.cols, rows: term.rows })
}

function fitNow() {
  if (disposed || !term || !fit || !host.value) return
  // Hidden panes (v-show) report a zero-size host; fitting there collapses the grid.
  if (!host.value.clientWidth || !host.value.clientHeight) return
  const dims = fit.proposeDimensions?.()
  if (!dims || !Number.isFinite(dims.cols) || !Number.isFinite(dims.rows)) return
  const cols = Math.max(2, dims.cols)
  const rows = Math.max(1, dims.rows)
  if (cols !== term.cols || rows !== term.rows) {
    term.resize(cols, rows)
    resize()
  }
}

function scheduleFit() {
  if (fitFrame) return
  fitFrame = requestAnimationFrame(() => {
    fitFrame = 0
    fitNow()
  })
}

function clearReconnect() {
  if (reconnectTimer) clearTimeout(reconnectTimer)
  reconnectTimer = undefined
}

// The agent CLI's own interactive screens (#112), read off the screen this pane holds. Checked
// at most once per settling interval: a TUI draws a menu in several writes. Restarting the
// timer on every write would keep a dismissed menu alive while the CLI animates or streams.
const MENU_SETTLE_MS = 150
let menuTimer
let lastMenu = null
function scheduleMenuCheck() {
  if (props.kind !== 'agent') return
  if (menuTimer) return
  menuTimer = setTimeout(() => {
    menuTimer = undefined
    checkMenu()
  }, MENU_SETTLE_MS)
}
function checkMenu() {
  if (disposed || !term) return
  const buffer = term.buffer.active
  const lines = []
  // baseY is the top of the live screen; viewportY would follow a person scrolling back.
  for (let y = 0; y < term.rows; y++) lines.push(buffer.getLine(buffer.baseY + y)?.translateToString(true) ?? '')
  reportMenu(isLive.value && ws ? detectMenu(lines) : null)
}
function reportMenu(menu) {
  if (sameMenu(menu, lastMenu)) return
  lastMenu = menu
  emit('menu', menu)
}

// Keys for the CLI as a person would press them, one message each with a short gap: a TUI that
// handles one key per read would otherwise see only the first of several sent together.
const KEY_GAP_MS = 40
function sendKeys(keys) {
  const socket = ws
  if (!canSend.value || socket?.readyState !== WebSocket.OPEN) return Promise.reject(new Error('Agent is disconnected'))
  return keys.reduce((previous, key, index) => previous.then(() => new Promise((resolve, reject) => {
    const write = () => {
      if (socket !== ws || socket.readyState !== WebSocket.OPEN) return reject(new Error('Agent is disconnected'))
      socket.send(JSON.stringify({ type: 'input', data: key }))
      resolve()
    }
    if (index === 0) write()
    else setTimeout(write, KEY_GAP_MS)
  })), Promise.resolve())
}

function closeSocket() {
  cancelSubmission?.()
  reportMenu(null)
  connectionGeneration += 1
  clearReconnect()
  const socket = ws
  ws = undefined
  if (socket) {
    socket.onclose = null
    socket.onmessage = null
    socket.onerror = null
    socket.onopen = null
    socket.close()
  }
}

function enableInput() {
  if (!inputRegistered && canSend.value && term) {
    inputRegistered = true
    term.onData(data => send({ type: 'input', data }))
  }
}

async function connect() {
  if (disposed || !isLive.value) return
  clearReconnect()
  const generation = ++connectionGeneration
  const sessionId = props.session.id
  const url = props.sharedToken
    ? sharedTerminalUrl(props.sharedToken)
    : await (props.kind === 'shell' ? shellUrl(sessionId) : terminalUrl(sessionId))
  if (disposed || generation !== connectionGeneration || sessionId !== props.session.id) return

  const socket = new WebSocket(url)
  ws = socket
  if (term) term.options.convertEol = false
  socket.onopen = () => {
    if (disposed || ws !== socket) return
    emit('status', 'connected')
    resize(true)
  }
  socket.onmessage = event => {
    if (!disposed && ws === socket && term) term.write(typeof event.data === 'string' ? event.data : new Uint8Array(event.data), scheduleMenuCheck)
  }
  socket.onclose = () => {
    if (disposed || ws !== socket) return
    cancelSubmission?.()
    ws = undefined
    reportMenu(null)
    emit('status', 'disconnected')
    if (isLive.value) reconnectTimer = setTimeout(connect, 2000)
  }
  socket.onerror = () => {
    if (!disposed && ws === socket) emit('status', 'error')
  }
}

async function transcript() {
  const sessionId = props.session.id
  emit('status', 'history')
  const text = props.sharedToken ? await getSharedTranscript(props.sharedToken) : await api.getTranscript(sessionId)
  if (disposed || sessionId !== props.session.id || !term) return
  // The saved transcript comes back with control sequences stripped, carriage returns
  // included, so its lines end in a bare "\n". xterm moves the cursor down but not back on
  // a bare line feed, and the replay rendered as a staircase. Only for the replay: a live
  // PTY sends its own "\r\n", and converting there would alter raw-mode output.
  term.options.convertEol = true
  term.write(text || '\r\n[no saved transcript]\r\n')
}

function reconnectForCurrentSession() {
  closeSocket()
  term?.clear?.()
  if (isLive.value) {
    enableInput()
    connect()
  } else {
    transcript()
  }
}

onMounted(() => {
  term = new Terminal({ fontFamily: "'JetBrains Mono', monospace", fontSize: 13, cursorBlink: canSend.value, disableStdin: !canSend.value, theme: { background: '#0e0d0b', foreground: '#c9c4bb' } })
  fit = new FitAddon()
  term.loadAddon(fit)
  // A shared link's viewer did not start the session, so its output may not write their clipboard.
  clipboard = attachTerminalClipboard(term, { allowOsc52: !props.sharedToken, host: host.value, notify: showPasteNotice,
    pasteFiles: props.kind === 'agent' ? pasteFilesIntoAgent : undefined })
  term.open(host.value)
  fitNow()
  ro = new ResizeObserver(scheduleFit)
  ro.observe(host.value)
  if (isLive.value) {
    enableInput()
    connect()
  } else {
    transcript()
  }
})

watch(() => props.session.id, reconnectForCurrentSession)
watch(isLive, (live, wasLive) => {
  if (term) {
    term.options.disableStdin = !canSend.value
    term.options.cursorBlink = canSend.value
  }
  if (live && !wasLive) {
    enableInput()
    connect()
  } else if (!live && wasLive) {
    closeSocket()
  }
})
watch(() => props.readonly, () => {
  if (term) term.options.disableStdin = !canSend.value
  enableInput()
})
watch(() => props.active, visible => {
  if (visible && term) scheduleFit()
})

onBeforeUnmount(() => {
  disposed = true
  clearTimeout(menuTimer)
  closeSocket()
  if (fitFrame) {
    cancelAnimationFrame(fitFrame)
    fitFrame = 0
  }
  ro?.disconnect()
  ro = undefined
  clipboard?.dispose()
  clipboard = undefined
  clearTimeout(pasteNoticeTimer)
  term?.dispose()
  term = undefined
  fit = undefined
})
</script>
<template><div class="pane"><div ref="host" class="term"></div><div v-if="pasteNotice" class="paste-notice" role="status" data-paste-notice>{{ pasteNotice }}</div><div v-if="showComposer && canSend && kind === 'agent'" class="composer"><input v-model="mobileInput" placeholder="Message the agent…" @keyup.enter="sendComposerMessage" /><button class="primary" @click="sendComposerMessage">Send</button></div></div></template>
<style scoped>.pane { position: relative; flex: 1; display: flex; flex-direction: column; min-width: 0; min-height: 0; background: #0e0d0b; } .term { flex: 1; min-height: 0; padding: 10px 12px; overflow: hidden; } .composer { display: flex; gap: 10px; padding: 12px 16px; background: var(--bg); border-top: 1px solid var(--border); } .composer input { flex: 1; background: var(--hover); border: 1px solid var(--border-2); border-radius: var(--radius); } .composer button { align-self: center; padding: 9px 18px; } .paste-notice { position: absolute; top: 10px; right: 16px; max-width: min(420px, calc(100% - 32px)); padding: 8px 12px; background: var(--bg); color: var(--text); border: 1px solid var(--border-2); border-radius: var(--radius); font-size: 13px; }</style>
