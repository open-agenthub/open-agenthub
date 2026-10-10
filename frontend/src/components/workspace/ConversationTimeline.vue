<script setup>
import { computed, nextTick, ref, watch } from 'vue'
import { renderMarkdown } from '../../lib/markdown.js'
import { renderMermaidBlocks } from '../../lib/mermaid.js'
import WorkLog from './WorkLog.vue'
import MessageActions from './MessageActions.vue'

const props = defineProps({ items: { type: Array, default: () => [] }, loading: Boolean, error: String, canQuote: Boolean, source: String })
defineEmits(['quote', 'retry'])
const scroller = ref(null)
const query = ref('')
const following = ref(true)
const visible = computed(() => {
  const search = query.value.trim().toLocaleLowerCase()
  return props.items.map((item, index) => ({ ...item, index }))
    .filter(item => !search || `${item.label} ${item.text}`.toLocaleLowerCase().includes(search))
})
function onScroll() {
  const el = scroller.value
  following.value = el.scrollHeight - el.scrollTop - el.clientHeight < 48
}
function latest() {
  query.value = ''
  following.value = true
  nextTick(() => { if (scroller.value) scroller.value.scrollTop = scroller.value.scrollHeight })
}
watch([() => props.items, query], async () => {
  await nextTick()
  if (following.value && !query.value && scroller.value) scroller.value.scrollTop = scroller.value.scrollHeight
  if (scroller.value) void renderMermaidBlocks(scroller.value)
}, { immediate: true })
</script>

<template>
  <section class="conversation" aria-label="Conversation" data-workspace-conversation>
    <div class="conversation-toolbar">
      <span>{{ source === 'native' ? 'Conversation' : 'Terminal history' }}</span>
      <input v-model="query" type="search" aria-label="Search conversation" placeholder="Find in conversation…">
    </div>
    <div ref="scroller" class="conversation-scroll" @scroll.passive="onScroll">
      <div class="timeline">
        <p v-if="loading && !items.length" class="empty" role="status">Loading conversation…</p>
        <div v-else-if="error" class="empty" role="alert">{{ error }} <button type="button" @click="$emit('retry')">Retry</button></div>
        <div v-else-if="!items.length" class="empty">
          <span class="empty-symbol" aria-hidden="true">✳</span>
          <h2>Your agent’s workspace</h2>
          <p>Give your agent a task. Follow the conversation here and open the terminal whenever you need it.</p>
        </div>
        <p v-else-if="!visible.length" class="empty">No matching messages.</p>
        <template v-for="item in visible" :key="item.index">
          <WorkLog v-if="item.role === 'tool' || item.role === 'result'" :label="item.label" :state="item.role === 'result' ? 'output' : ''">
            <pre>{{ item.text }}</pre>
          </WorkLog>
          <article v-else class="message" :class="'message-' + item.role" :data-workspace-role="item.role">
            <span class="message-label">{{ item.label }}</span>
            <div v-if="item.role === 'assistant'" class="workspace-markdown" v-html="renderMarkdown(item.text)"></div>
            <pre v-else>{{ item.text }}</pre>
            <MessageActions :text="item.text" :can-quote="canQuote" @quote="$emit('quote', $event)" />
          </article>
        </template>
      </div>
    </div>
    <button v-if="!following && items.length" class="jump-latest" type="button" @click="latest">↓ Jump to latest</button>
  </section>
</template>

<style scoped>
.conversation { position: relative; display: flex; flex: 1; flex-direction: column; min-height: 0; min-width: 0; }
.conversation-toolbar { display: flex; align-items: center; justify-content: space-between; gap: 12px; padding: 10px 20px; color: var(--muted-3); font-size: 11px; }
.conversation-toolbar input { width: min(240px, 55%); border: 1px solid transparent; background: transparent; padding: 5px 8px; font-size: 12px; }
.conversation-toolbar input:focus { border-color: var(--border-2); background: var(--panel); }
.conversation-scroll { flex: 1; overflow-y: auto; min-height: 0; }
.timeline { display: flex; flex-direction: column; max-width: 840px; margin: auto; padding: 16px 32px 36px; gap: 12px; }
.message { min-width: 0; font-size: 14px; line-height: 1.7; padding: 10px 0; }
.message-label { display: block; margin-bottom: 8px; color: var(--muted-3); font-size: 11px; font-weight: 600; }
.message-user { align-self: flex-end; max-width: 88%; padding: 14px 18px; border: 1px solid var(--border); background: var(--panel-2); border-radius: 16px; }
.message pre { margin: 0; white-space: pre-wrap; overflow-wrap: anywhere; font: inherit; }
.message-terminal pre { font: 12px/1.6 var(--mono); }
.empty { text-align: center; color: var(--muted-3); padding: 36px 16px; font-size: 13px; }
.empty-symbol { display: block; color: var(--accent); font-size: 36px; margin: 24px 0 16px; }
.empty h2 { color: var(--strong); font-size: 24px; font-weight: 500; }
.empty p { max-width: 380px; margin: auto; line-height: 1.7; }
.jump-latest { position: absolute; bottom: 16px; left: 50%; transform: translateX(-50%); border-radius: 20px; padding: 7px 14px; font-size: 12px; box-shadow: 0 4px 16px #0003; }
@media(max-width: 640px) { .timeline { padding-inline: 16px; } .conversation-toolbar { padding-inline: 12px; } }
</style>
