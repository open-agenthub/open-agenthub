<script setup>
import { ref, reactive, computed } from 'vue'
import TerminalPane from './TerminalPane.vue'
import { canPause, tabLabel } from '../lib/status.js'

const props = defineProps({ session: Object })
const emit = defineEmits(['back', 'resume', 'pause'])

// Live sessions offer a second "Shell" tab (interactive bash in the same pod).
const isLive = computed(() => props.session?.phase === 'Running' || props.session?.phase === 'Pending')
const showPause = computed(() => canPause(props.session))

const activeTab = ref('agent')            // 'agent' | 'shell'
const shellOpened = ref(false)            // spawn the shell only once its tab is first opened
const statuses = reactive({ agent: 'connecting…', shell: '' })

function selectTab(kind) {
  if (kind === 'shell') shellOpened.value = true
  activeTab.value = kind
}

const barStatus = computed(() => statuses[activeTab.value])
</script>

<template>
  <div class="term-wrap">
    <div class="term-bar">
      <button class="back" @click="$emit('back')">‹ Back</button>
      <span class="title">{{ session.title }}</span>

      <nav v-if="isLive" class="tabs">
        <button :class="{ on: activeTab === 'agent' }" @click="selectTab('agent')">{{ tabLabel('agent') }}</button>
        <button :class="{ on: activeTab === 'shell' }" @click="selectTab('shell')">{{ tabLabel('shell') }}</button>
      </nav>

      <span class="right">
        <button v-if="showPause" class="pause" title="Pause (save state & stop the pod)"
                @click="$emit('pause', session.id)">⏸ Pause</button>
        <button v-if="session.canResume" class="resume" @click="$emit('resume', session.id)">↻ Resume</button>
        <span class="status" :class="barStatus">{{ barStatus }}</span>
      </span>
    </div>

    <!-- Agent pane is always mounted; the shell pane is spawned on demand and then
         kept alive (v-show) so switching tabs does not drop its session. -->
    <TerminalPane v-show="activeTab === 'agent'" :session="session" kind="agent"
      :active="activeTab === 'agent'" @status="s => statuses.agent = s" />
    <TerminalPane v-if="isLive && shellOpened" v-show="activeTab === 'shell'" :session="session" kind="shell"
      :active="activeTab === 'shell'" @status="s => statuses.shell = s" />
  </div>
</template>

<style scoped>
.term-wrap { flex: 1; display: flex; flex-direction: column; min-width: 0; background: #0E1116; }
.term-bar { display: flex; align-items: center; gap: 12px; padding: 8px 14px; border-bottom: 1px solid var(--border); background: var(--panel); }
.term-bar .title { flex: 1; font-size: 13px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.term-bar .right { display: flex; align-items: center; gap: 10px; }
.tabs { display: flex; gap: 4px; }
.tabs button { font-size: 12px; padding: 4px 12px; color: var(--muted); background: none; border: 1px solid transparent; border-radius: var(--radius); }
.tabs button.on { color: var(--text); border-color: var(--border); background: var(--panel-2); }
.tabs button:hover { color: var(--accent); }
.back { background: none; border: none; color: var(--muted); }
.back:hover { color: var(--accent); border: none; }
.pause, .resume { font-size: 12px; padding: 5px 10px; }
.pause:hover { border-color: var(--accent); color: var(--accent); }
.resume:hover { border-color: var(--running); color: var(--running); }
.status { font-family: var(--mono); font-size: 12px; color: var(--muted); }
.status.connected { color: var(--running); }
.status.disconnected, .status.error { color: var(--danger); }
</style>
