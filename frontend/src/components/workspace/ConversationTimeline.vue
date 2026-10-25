<script setup>
import { computed, nextTick, ref, watch } from 'vue'
import { renderMarkdown } from '../../lib/markdown.js'
import { renderMermaidBlocks } from '../../lib/mermaid.js'
import { fullMessageTime, isoMessageTime, shortMessageTime } from '../../lib/message-time.js'
import WorkLog from './WorkLog.vue'
import MessageActions from './MessageActions.vue'
import ConversationImage from './ConversationImage.vue'

// inviting: the person can give this session a task. Otherwise — a finished run, a read-only
// share, an autonomous session — an empty conversation is a record with nothing saved in it,
// and "Give your agent a task" would point at a composer that is not there.
// pending: sent or fleet messages the agent has not taken yet (#153), { key, label, state, text,
// dismissible, sendNow, interruptSend }; shown after the conversation and gone once the
// conversation holds them. sendNow / interruptSend offer to deliver one at once (#261).
const props = defineProps({ sessionId: String, sharedToken: String, items: { type: Array, default: () => [] }, loading: Boolean, error: String, canQuote: Boolean, source: String, inviting: Boolean,
  pending: { type: Array, default: () => [] } })
defineEmits(['quote', 'retry', 'dismiss-pending', 'send-pending'])
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
watch([() => props.items, () => props.pending, query], async () => {
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
        <p v-else-if="!items.length && !inviting" class="empty" data-conversation-empty>No saved conversation.</p>
        <div v-else-if="!items.length && !pending.length" class="empty">
          <span class="empty-symbol" aria-hidden="true">✳</span>
          <h2>Your agent’s workspace</h2>
          <p>Give your agent a task. Follow the conversation here and open the terminal whenever you need it.</p>
        </div>
        <p v-else-if="!visible.length" class="empty">No matching messages.</p>
        <template v-for="item in visible" :key="item.index">
          <div v-if="item.role === 'boundary'" class="conversation-boundary" role="separator" data-conversation-boundary>{{ item.text }}</div>
          <WorkLog v-else-if="item.role === 'tool' || item.role === 'result'" :label="item.label" :state="item.role === 'result' ? 'output' : ''">
            <pre>{{ item.text }}</pre>
          </WorkLog>
          <article v-else class="message" :class="'message-' + item.role" :data-workspace-role="item.role">
            <span class="message-label">{{ item.label }}<time v-if="item.at" class="message-time" :datetime="isoMessageTime(item.at)"
              :title="fullMessageTime(item.at)" data-message-time>{{ shortMessageTime(item.at) }}</time></span>
            <!-- What the person or the model wrote is markdown, as in the streaming Workspace;
                 scrollback text stays verbatim, where markdown rules would mangle a listing. -->
            <div v-if="item.role === 'assistant' || item.role === 'user' || item.role === 'fleet'" class="md" v-html="renderMarkdown(item.text)"></div>
            <pre v-else>{{ item.text }}</pre>
            <ConversationImage v-for="file in item.attachments || []" :key="file.id" :file="file" :session-id="sessionId" :shared-token="sharedToken" />
            <MessageActions :text="item.text" :can-quote="canQuote" @quote="$emit('quote', $event)" />
          </article>
        </template>
        <ol v-if="pending.length && !query.trim()" class="pending" aria-label="Waiting for the agent" data-pending-messages>
          <li v-for="entry in pending" :key="entry.key" class="message message-pending" :class="'pending-' + entry.kind" data-pending-message :data-pending-kind="entry.kind">
            <span class="message-label">{{ entry.label }} <span class="pending-state">· {{ entry.state }}</span><time v-if="entry.at" class="message-time"
              :datetime="isoMessageTime(entry.at)" :title="fullMessageTime(entry.at)" data-message-time>{{ shortMessageTime(entry.at) }}</time></span>
            <pre>{{ entry.text }}</pre>
            <p v-if="entry.sendNow || entry.interruptSend || entry.note" class="pending-actions">
              <button v-if="entry.sendNow" type="button" :disabled="entry.busy" data-pending-send-now
                title="Deliver it into the agent's running prompt now, as a priority message"
                @click="$emit('send-pending', entry.key, { interrupt: false })">Send now</button>
              <button v-if="entry.interruptSend" type="button" :disabled="entry.busy" data-pending-interrupt
                :title="entry.kind === 'sent' ? 'Stop the agent\'s turn; it then takes this message at once' : 'Stop the agent\'s turn and deliver this message now'"
                @click="$emit('send-pending', entry.key, { interrupt: true })">Interrupt and send</button>
              <span v-if="entry.note" class="pending-note" role="status">{{ entry.note }}</span>
            </p>
            <button v-if="entry.dismissible" type="button" class="pending-dismiss" aria-label="Stop showing as pending" title="Stop showing as pending"
              data-pending-dismiss @click="$emit('dismiss-pending', entry.key)">✕</button>
          </li>
        </ol>
      </div>
    </div>
    <button v-if="!following && items.length" class="jump-latest" type="button" @click="latest">↓ Jump to latest</button>
  </section>
</template>

<style scoped>
.conversation { position: relative; display: flex; flex: 1; flex-direction: column; min-height: 0; min-width: 0; overflow: hidden; }
.conversation-toolbar { display: flex; align-items: center; justify-content: space-between; gap: 12px; padding: 10px 20px; color: var(--muted-3); font-size: 11px; }
.conversation-toolbar input { width: min(240px, 55%); border: 1px solid transparent; background: transparent; padding: 5px 8px; font-size: 12px; }
.conversation-toolbar input:focus { border-color: var(--border-2); background: var(--panel); }
.conversation-scroll { flex: 1; overflow-y: auto; min-height: 0; }
.timeline { display: flex; flex-direction: column; max-width: 840px; margin: auto; padding: 16px 32px 36px; gap: 12px; }
.conversation-boundary { display: flex; align-items: center; gap: 12px; color: var(--muted-3); font-size: 12px; padding: 16px 0; }
.conversation-boundary::before, .conversation-boundary::after { content: ""; flex: 1; border-top: 1px solid var(--border-2); }
.message { min-width: 0; font-size: 14px; line-height: 1.7; padding: 10px 0; }
.message-label { display: block; margin-bottom: 8px; color: var(--muted-3); font-size: 11px; font-weight: 600; }
.message-user { align-self: flex-end; max-width: 88%; padding: 14px 18px; border: 1px solid var(--border); background: var(--panel-2); border-radius: 16px; }
.message-fleet { align-self: flex-start; max-width: 88%; padding: 14px 18px; border: 1px solid var(--border); border-left: 3px solid var(--accent); background: var(--panel-2); border-radius: 16px; }
.pending { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 8px; }
.message-pending { position: relative; align-self: flex-end; max-width: 88%; padding: 10px 34px 10px 16px; border: 1px dashed var(--border-2); border-radius: 16px; color: var(--muted); font-size: 13px; }
.message-pending.pending-fleet { align-self: flex-start; }
.message-pending .message-label { margin-bottom: 4px; }
.pending-state { font-weight: 400; }
.message-pending pre { display: -webkit-box; -webkit-line-clamp: 4; -webkit-box-orient: vertical; overflow: hidden; }
.pending-dismiss { position: absolute; top: 6px; right: 6px; padding: 2px 6px; border: 0; background: transparent; color: var(--muted-3); font-size: 11px; }
.pending-dismiss:hover { color: var(--text); }
.pending-actions { display: flex; flex-wrap: wrap; align-items: center; gap: 6px; margin: 8px 0 0; }
.pending-actions button { padding: 3px 9px; border-radius: 8px; font-size: 11px; }
.pending-note { color: var(--muted-3); font-size: 11px; }
.message-time { margin-left: 8px; font-weight: 400; color: var(--muted-3); cursor: default; }
.message pre { margin: 0; white-space: pre-wrap; overflow-wrap: anywhere; font: inherit; }
.message-terminal pre { font: 12px/1.6 var(--mono); }
.empty { text-align: center; color: var(--muted-3); padding: 36px 16px; font-size: 13px; }
.empty-symbol { display: block; color: var(--accent); font-size: 36px; margin: 24px 0 16px; }
.empty h2 { color: var(--strong); font-size: 24px; font-weight: 500; }
.empty p { max-width: 380px; margin: auto; line-height: 1.7; }
.jump-latest { position: absolute; bottom: 16px; left: 50%; transform: translateX(-50%); border-radius: 20px; padding: 7px 14px; font-size: 12px; box-shadow: 0 4px 16px #0003; }
@media(max-width: 640px) { .timeline { padding-inline: 16px; } .conversation-toolbar { padding-inline: 12px; } }
</style>
