<script setup>
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { Terminal } from '@xterm/xterm'
import { FitAddon } from '@xterm/addon-fit'
import { api, getSharedTranscript, sharedTerminalUrl, shellUrl, terminalUrl } from '../api.js'
const props = defineProps({ session: Object, kind: { type: String, default: 'agent' }, active: { type: Boolean, default: true }, readonly: { type: Boolean, default: false }, sharedToken: { type: String, default: null } }); const emit = defineEmits(['status'])
const host = ref(null); const mobileInput = ref(''); let term, fit, ws, ro
const isLive = computed(() => props.kind === 'shell' || ['Running', 'Pending'].includes(props.session?.phase)); const canSend = computed(() => isLive.value && !props.readonly)
function send(value) { if (canSend.value && ws?.readyState === WebSocket.OPEN) ws.send(JSON.stringify(value)) }
function resize() { if (term) send({ type: 'resize', cols: term.cols, rows: term.rows }) }
async function connect() { const url = props.sharedToken ? sharedTerminalUrl(props.sharedToken) : await (props.kind === 'shell' ? shellUrl(props.session.id) : terminalUrl(props.session.id)); const socket = new WebSocket(url); ws = socket; socket.onopen = () => { emit('status', 'connected'); resize() }; socket.onmessage = event => term.write(typeof event.data === 'string' ? event.data : new Uint8Array(event.data)); socket.onclose = () => { emit('status', 'disconnected'); if (isLive.value) setTimeout(() => term && connect(), 2000) }; socket.onerror = () => emit('status', 'error') }
async function transcript() { emit('status', 'history'); const text = props.sharedToken ? await getSharedTranscript(props.sharedToken) : await api.getTranscript(props.session.id); term.write(text || '\r\n[no saved transcript]\r\n') }
onMounted(() => { term = new Terminal({ fontFamily: "'JetBrains Mono', monospace", fontSize: 13, cursorBlink: canSend.value, disableStdin: !canSend.value, theme: { background: '#0E1116', foreground: '#D7DCE3' } }); fit = new FitAddon(); term.loadAddon(fit); term.open(host.value); fit.fit(); ro = new ResizeObserver(() => { fit.fit(); resize() }); ro.observe(host.value); if (isLive.value) { if (canSend.value) term.onData(data => send({ type: 'input', data })); connect() } else transcript() })
watch(() => props.active, visible => { if (visible && term) { fit.fit(); resize() } })
onBeforeUnmount(() => { ro?.disconnect(); ws?.close(); term?.dispose() })
</script>
<template><div class="pane"><div ref="host" class="term"></div><div v-if="canSend && kind === 'agent'" class="mobile-input"><input v-model="mobileInput" placeholder="Type a reply and send…" @keyup.enter="send({ type: 'input', data: mobileInput + '\r' }); mobileInput = ''" /><button class="primary" @click="send({ type: 'input', data: mobileInput + '\r' }); mobileInput = ''">Send</button></div></div></template>
<style scoped>.pane { flex: 1; display: flex; flex-direction: column; min-width: 0; min-height: 0; background: #0E1116; } .term { flex: 1; min-height: 0; padding: 8px 10px; } .mobile-input { display: none; gap: 8px; padding: 10px; background: var(--panel); } .mobile-input input { flex: 1; } @media (max-width: 760px) { .mobile-input { display: flex; } }</style>
