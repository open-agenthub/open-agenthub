<script setup>
import { computed, ref, watch, onBeforeUnmount } from 'vue'
import { fileIcon, fileSize } from '../lib/files.js'
import { createAttachmentQueue } from '../lib/attachments.js'
import { api } from '../api.js'
import FilePreview from './FilePreview.vue'
import ChatAttachments from './ChatAttachments.vue'

const props = defineProps({
  sessionId: { type: String, required: true },
  files: { type: Array, default: () => [] },
  selectedId: { type: String, default: null },
  capabilities: { type: Object, default: () => ({}) },
  canWrite: { type: Boolean, default: false },
  sharedToken: { type: String, default: null },
  presented: { type: Boolean, default: false }
})
const emit = defineEmits(['select', 'close', 'dismiss', 'uploaded'])
const selected = computed(() => props.files.find(file => file.id === props.selectedId) || null)

// Uploading is the same reserve/upload/complete flow the chat pane uses; the difference
// is that nothing is attached to a turn afterwards — the file simply joins the session.
const canUpload = computed(() => props.canWrite && !props.sharedToken)
const dragging = ref(false)
const fileInput = ref(null)
const uploadVersion = ref(0)
let queue = makeQueue()
const uploadItems = computed(() => {
  void uploadVersion.value
  return queue.items.slice()
})

function makeQueue() {
  return createAttachmentQueue({
    sessionId: props.sessionId,
    api,
    onChange: items => {
      uploadVersion.value += 1
      // Show the file as soon as it lands rather than waiting for the next poll.
      if (items.some(item => item.state === 'ready')) emit('uploaded')
    }
  })
}

function addFiles(files) {
  if (canUpload.value && files?.length) queue.add(files)
}

function pick(event) {
  addFiles(event.target.files)
  event.target.value = ''
}

function drop(event) {
  dragging.value = false
  addFiles(event.dataTransfer?.files)
}

// A finished upload belongs to the session, so keep the rows only until they are listed.
function clearFinished() {
  queue.clearReady()
  uploadVersion.value += 1
}

// Hand the row over to the listing as soon as the file appears there, so the same file is never
// shown twice and the queue stops holding a reference to something it no longer owns.
watch(() => props.files, listed => {
  for (const item of queue.items.slice()) {
    if (item.state === 'ready' && listed.some(file => file.id === item.id)) queue.release(item.key)
  }
})

watch(() => props.sessionId, () => {
  void queue.cancelAll()
  queue = makeQueue()
  uploadVersion.value += 1
})
onBeforeUnmount(() => { void queue.cancelAll() })
</script>

<template>
  <section class="files-pane" data-files-pane
    :class="{ dropping: dragging && canUpload }"
    @dragover.prevent="dragging = canUpload"
    @dragleave="dragging = false"
    @drop.prevent="drop">
    <aside class="file-rail">
      <header>
        <strong>Files</strong><span>{{ files.length }}</span>
        <button v-if="canUpload" type="button" class="upload-btn" data-files-upload
          title="Upload files to this session" @click="fileInput?.click()">Upload</button>
      </header>
      <input v-if="canUpload" ref="fileInput" type="file" multiple class="file-input"
        data-files-input @change="pick" />
      <div v-if="!files.length && !uploadItems.length" class="empty">
        Images and documents created by the agent appear here.<template v-if="canUpload"> Drop files anywhere in this pane to add your own.</template>
      </div>
      <div v-if="uploadItems.length" class="uploads" data-files-uploads>
        <!-- dismiss, not remove: here the × cancels an upload in flight, but a finished one is
             already a session file and this list offers no other way to delete anything. -->
        <ChatAttachments :items="uploadItems" :retry="queue.retry" :remove="queue.dismiss" />
        <button type="button" class="clear-done" data-files-clear @click="clearFinished">Clear finished</button>
      </div>
      <button v-for="file in files" :key="file.id" type="button" class="file-row"
        :class="{ active: file.id === selectedId }" :data-file="file.id" @click="$emit('select', file.id)">
        <span class="file-icon">{{ fileIcon(file) }}</span>
        <span class="file-name"><strong>{{ file.name }}</strong><small>{{ fileSize(file.size) }}</small></span>
      </button>
    </aside>
    <div class="file-main">
      <header class="file-toolbar">
        <span>{{ selected?.name || 'Preview' }}</span>
        <span class="toolbar-actions">
          <button v-if="presented && canWrite" type="button" data-files-dismiss @click="$emit('dismiss')">Dismiss for everyone</button>
          <button type="button" data-files-close aria-label="Close files" @click="$emit('close')">×</button>
        </span>
      </header>
      <FilePreview :session-id="sessionId" :file="selected" :capabilities="capabilities" :shared-token="sharedToken" />
    </div>
  </section>
</template>

<style scoped>
.files-pane { display: grid; grid-template-columns: minmax(150px, 31%) minmax(0, 1fr); height: 100%; min-height: 0; color: #dce5f1; background: #0c1118; }
.files-pane.dropping { outline: 2px dashed var(--accent); outline-offset: -6px; }
.file-input { display: none; }
.upload-btn { margin-left: auto; padding: 3px 10px; border: 1px solid #29303a; border-radius: 7px; background: none; color: #dce5f1; font-size: 11px; }
.upload-btn:hover { border-color: var(--accent); color: var(--accent); }
.uploads { padding: 8px 10px; border-bottom: 1px solid #29303a; }
.clear-done { margin-top: 6px; padding: 2px 8px; border: none; background: none; color: #8b94a3; font-size: 11px; }
.clear-done:hover { color: var(--accent); }
.file-rail { min-width: 0; overflow-y: auto; border-right: 1px solid #29303a; background: #121821; }
.file-rail > header { position: sticky; top: 0; z-index: 1; display: flex; align-items: center; justify-content: space-between; height: 34px; padding: 0 10px; border-bottom: 1px solid #29303a; background: #171c24; font: 10px var(--mono); text-transform: uppercase; }
.file-rail > header span { color: #738094; }
.file-row { width: 100%; display: grid; grid-template-columns: 32px minmax(0, 1fr); gap: 8px; align-items: center; padding: 8px 9px; border: 0; border-bottom: 1px solid #202936; border-radius: 0; color: #aeb8c8; background: transparent; text-align: left; }
.file-row:hover { background: #18212d; }
.file-row.active { color: white; background: #1c2938; box-shadow: inset 2px 0 var(--accent); }
.file-icon { display: grid; place-items: center; width: 30px; height: 30px; border: 1px solid #354255; border-radius: 6px; color: var(--accent); font: 700 8px var(--mono); }
.file-name { min-width: 0; display: flex; flex-direction: column; gap: 2px; }
.file-name strong { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-size: 11px; }
.file-name small { color: #738094; font: 9px var(--mono); }
.empty { padding: 18px 12px; color: #738094; font-size: 11px; line-height: 1.5; }
.file-main { min-width: 0; min-height: 0; display: grid; grid-template-rows: 34px minmax(0, 1fr); }
.file-toolbar { display: flex; align-items: center; justify-content: space-between; gap: 8px; padding: 0 10px; border-bottom: 1px solid #29303a; background: #171c24; color: #aeb8c8; font: 10px var(--mono); }
.toolbar-actions { display: flex; align-items: center; gap: 5px; }
.file-toolbar button { padding: 3px 7px; border: 0; color: #8e9bad; background: transparent; font-size: 10px; }
.file-toolbar button:hover { color: white; background: #252e3a; }
@media (max-width: 520px) { .files-pane { grid-template-columns: 110px minmax(0, 1fr); } .file-icon { display: none; } .file-row { grid-template-columns: 1fr; } }
</style>
