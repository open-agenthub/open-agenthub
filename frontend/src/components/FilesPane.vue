<script setup>
import { computed, ref, watch, onBeforeUnmount } from 'vue'
import { fileIcon, fileSize, saveBlob } from '../lib/files.js'
import { createAttachmentQueue } from '../lib/attachments.js'
import { api, getSessionFileContent, getSharedSessionFileContent } from '../api.js'
import FilePreview from './FilePreview.vue'
import ChatAttachments from './ChatAttachments.vue'

const props = defineProps({
  sessionId: { type: String, required: true },
  files: { type: Array, default: () => [] },
  selectedId: { type: String, default: null },
  capabilities: { type: Object, default: () => ({}) },
  canWrite: { type: Boolean, default: false },
  sharedToken: { type: String, default: null },
  presented: { type: Boolean, default: false },
  presentedId: { type: String, default: null },
  inProject: { type: Boolean, default: false }
})
const emit = defineEmits(['select', 'dismiss', 'uploaded', 'deleted'])
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

// Deleting is asked for in the row itself rather than with confirm(): a native dialog blocks the
// whole tab, the session's live updates included, and an embedding frame can refuse it outright,
// in which case confirm() answers false and the button silently does nothing.
const canDelete = computed(() => props.canWrite && !props.sharedToken)
const confirmingId = ref(null)
const deletingId = ref(null)
const deleteError = ref('')
// A converted Office preview belongs to its document and the backend refuses to delete it alone;
// offering the button would only lead to that refusal.
const previewIds = computed(() => new Set(props.files.map(file => file.previewFileId).filter(Boolean)))

function deletable(file) {
  return canDelete.value && file.state === 'Ready' && !previewIds.value.has(file.id)
}

function askDelete(file) {
  deleteError.value = ''
  confirmingId.value = file.id
}

function keep() {
  confirmingId.value = null
}

const DELETE_COPY = Object.freeze({
  file_is_preview: 'is the preview of a document. Delete the document instead.',
  file_upload_pending: 'is still uploading.',
})

async function confirmDelete(file) {
  if (deletingId.value) return
  deletingId.value = file.id
  deleteError.value = ''
  try {
    await api.deleteSessionFile(props.sessionId, file.id)
    confirmingId.value = null
    emit('deleted', file.id)
  } catch (error) {
    // A file someone else deleted first is gone either way, which is what was asked for.
    if (error?.status === 404) {
      confirmingId.value = null
      emit('deleted', file.id)
    } else if (error?.status === 403) {
      deleteError.value = `You cannot delete files in this session.`
    } else {
      deleteError.value = `${file.name} ${DELETE_COPY[error?.code] || 'could not be deleted.'}`
    }
  } finally {
    deletingId.value = null
  }
}

watch(() => props.sessionId, () => { confirmingId.value = null; deleteError.value = '' })

const downloading = ref(new Set())
const downloadError = ref('')

// Always the file's own id, fetched whole: the preview next door may be holding the first 1 MiB
// of a text file or the PDF rendered from an Office document, and neither is the file. Reading is
// all this needs, so a viewer of a shared session gets it through the same token the listing and
// the preview already use.
async function download(file) {
  if (downloading.value.has(file.id)) return
  downloading.value = new Set(downloading.value).add(file.id)
  downloadError.value = ''
  try {
    const blob = props.sharedToken
      ? await getSharedSessionFileContent(props.sharedToken, file.id)
      : await getSessionFileContent(props.sessionId, file.id)
    saveBlob(blob, file.name)
  } catch {
    downloadError.value = `${file.name} could not be downloaded.`
  } finally {
    const rest = new Set(downloading.value)
    rest.delete(file.id)
    downloading.value = rest
  }
}
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
      <!-- Two sibling buttons, not one inside the other: a nested button is invalid markup and
           a click on the download would select the row as well. -->
      <div v-for="file in files" :key="file.id" class="file-row" :class="{ active: file.id === selectedId }">
        <button type="button" class="file-select" :class="{ active: file.id === selectedId }" :data-file="file.id"
          :aria-current="file.id === selectedId || undefined" @click="$emit('select', file.id)">
          <span class="file-icon">{{ fileIcon(file) }}</span>
          <span class="file-name"><strong>{{ file.name }}</strong><small>{{ fileSize(file.size) }}</small></span>
        </button>
        <span class="row-actions">
          <button v-if="file.state !== 'Expired'" type="button" class="file-download" :data-file-download="file.id"
            :disabled="downloading.has(file.id)" :title="`Download ${file.name}`"
            :aria-label="`Download ${file.name}`" @click="download(file)">↓</button>
          <button v-if="deletable(file)" type="button" class="file-delete" :data-file-delete="file.id"
            :disabled="deletingId === file.id" :aria-expanded="confirmingId === file.id"
            :title="`Delete ${file.name}`" :aria-label="`Delete ${file.name}`" @click="askDelete(file)">×</button>
        </span>
        <div v-if="confirmingId === file.id" class="delete-confirm" role="group"
          :aria-label="`Delete ${file.name}?`" data-file-delete-confirm @keydown.esc="keep">
          <p><strong>Delete {{ file.name }}?</strong> It is removed for everyone with access to this session.</p>
          <p v-if="file.id === presentedId" data-file-delete-presented>It is being presented and the presentation closes.</p>
          <p v-if="file.previewFileId" data-file-delete-preview>Its PDF preview is deleted with it.</p>
          <p v-if="inProject" data-file-delete-project>Other sessions in this project stop seeing it. Copies they already fetched stay.</p>
          <p v-if="deleteError" class="delete-error" role="alert" data-file-delete-error>{{ deleteError }}</p>
          <span class="confirm-actions">
            <button type="button" class="ghost" data-file-delete-keep @click="keep">Keep</button>
            <button type="button" class="danger" data-file-delete-confirm-button :disabled="deletingId === file.id"
              @click="confirmDelete(file)">{{ deletingId === file.id ? 'Deleting…' : 'Delete' }}</button>
          </span>
        </div>
      </div>
    </aside>
    <div class="file-main">
      <header class="file-toolbar">
        <span class="toolbar-title">{{ selected?.name || 'Preview' }}</span>
        <span v-if="downloadError" class="download-error" role="alert" data-files-download-error>{{ downloadError }}</span>
        <span class="toolbar-actions">
          <button v-if="selected && selected.state !== 'Expired'" type="button" data-files-download
            :disabled="downloading.has(selected.id)" @click="download(selected)">
            {{ downloading.has(selected.id) ? 'Downloading…' : 'Download' }}</button>
          <button v-if="presented && canWrite" type="button" data-files-dismiss @click="$emit('dismiss')">Dismiss for everyone</button>
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
.file-row { display: grid; grid-template-columns: minmax(0, 1fr) auto; align-items: center; border-bottom: 1px solid #202936; color: #aeb8c8; }
.file-row:hover { background: #18212d; }
.file-select { min-width: 0; display: grid; grid-template-columns: 32px minmax(0, 1fr); gap: 8px; align-items: center; padding: 8px 9px; border: 0; border-radius: 0; color: inherit; background: transparent; text-align: left; }
.file-download { margin-right: 6px; padding: 4px 8px; border: 1px solid transparent; border-radius: 6px; color: #8e9bad; background: transparent; font-size: 12px; }
.file-download:hover:not(:disabled) { border-color: var(--accent); color: var(--accent); }
.file-download:disabled { opacity: .45; }
.row-actions { display: flex; align-items: center; }
.file-delete { margin-right: 6px; padding: 4px 8px; border: 1px solid transparent; border-radius: 6px; color: #8e9bad; background: transparent; font-size: 12px; }
.file-delete:hover:not(:disabled), .file-delete[aria-expanded="true"] { border-color: var(--danger); color: var(--danger); }
.file-delete:disabled { opacity: .45; }
.delete-confirm { grid-column: 1 / -1; display: flex; flex-direction: column; gap: 4px; margin: 0 8px 8px; padding: 8px 10px; border: 1px solid var(--danger); border-radius: 7px; background: #1a1418; font-size: 11px; line-height: 1.45; }
.delete-confirm p { margin: 0; color: #aeb8c8; }
.delete-confirm strong { color: white; overflow-wrap: anywhere; }
.delete-error { color: var(--danger) !important; }
.confirm-actions { display: flex; justify-content: flex-end; gap: 6px; margin-top: 4px; }
.confirm-actions button { padding: 3px 10px; border-radius: 6px; font-size: 11px; }
.confirm-actions .ghost { border: 1px solid #29303a; color: #dce5f1; background: none; }
.confirm-actions .danger { border: 1px solid var(--danger); color: white; background: var(--danger); }
.confirm-actions .danger:disabled { opacity: .6; }
.file-row.active { color: white; background: #1c2938; box-shadow: inset 2px 0 var(--accent); }
.file-icon { display: grid; place-items: center; width: 30px; height: 30px; border: 1px solid #354255; border-radius: 6px; color: var(--accent); font: 700 8px var(--mono); }
.file-name { min-width: 0; display: flex; flex-direction: column; gap: 2px; }
.file-name strong { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-size: 11px; }
.file-name small { color: #738094; font: 9px var(--mono); }
.empty { padding: 18px 12px; color: #738094; font-size: 11px; line-height: 1.5; }
.file-main { min-width: 0; min-height: 0; display: grid; grid-template-rows: 34px minmax(0, 1fr); }
.file-toolbar { display: flex; align-items: center; justify-content: space-between; gap: 8px; padding: 0 10px; border-bottom: 1px solid #29303a; background: #171c24; color: #aeb8c8; font: 10px var(--mono); }
.toolbar-title { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.download-error { min-width: 0; margin-left: auto; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; color: var(--danger); }
.toolbar-actions { flex: none; display: flex; align-items: center; gap: 5px; }
.file-toolbar button { padding: 3px 7px; border: 0; color: #8e9bad; background: transparent; font-size: 10px; }
.file-toolbar button:hover { color: white; background: #252e3a; }
@media (max-width: 520px) { .files-pane { grid-template-columns: 110px minmax(0, 1fr); } .file-icon { display: none; } .file-select { grid-template-columns: 1fr; } }
</style>
