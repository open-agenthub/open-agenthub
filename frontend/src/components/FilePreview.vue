<script setup>
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue'
import { getSessionFileContent, getSharedSessionFileContent } from '../api.js'
import { previewKind } from '../lib/files.js'
import { renderMarkdown } from '../lib/markdown.js'
import { renderMermaidBlocks } from '../lib/mermaid.js'

const props = defineProps({
  sessionId: { type: String, required: true },
  file: { type: Object, default: null },
  capabilities: { type: Object, default: () => ({}) },
  sharedToken: { type: String, default: null }
})

const MAX_TEXT_BYTES = 1024 * 1024
const loading = ref(false)
const error = ref('')
const objectUrl = ref('')
const markdownEl = ref(null)
const text = ref('')
const truncated = ref(false)
let generation = 0
const kind = computed(() => previewKind(props.file, props.capabilities))
// The backend already reports why a type cannot be shown; without surfacing it, an Office
// document looked simply unsupported, when in fact one setting away from working.
const officePreviewDisabled = computed(() =>
  props.capabilities?.unavailableReasons?.[props.file?.mimeType] === 'office_preview_disabled')

function releaseUrl() {
  if (objectUrl.value) URL.revokeObjectURL(objectUrl.value)
  objectUrl.value = ''
}

async function load() {
  const current = ++generation
  releaseUrl()
  text.value = ''
  truncated.value = false
  error.value = ''
  if (!props.file || ['pending', 'expired'].includes(kind.value)) return
  loading.value = true
  try {
    const targetId = kind.value === 'office-pdf' ? props.file.previewFileId : props.file.id
    const blob = props.sharedToken
      ? await getSharedSessionFileContent(props.sharedToken, targetId)
      : await getSessionFileContent(props.sessionId, targetId)
    if (current !== generation) return
    if (kind.value === 'markdown' || kind.value === 'text') {
      truncated.value = blob.size > MAX_TEXT_BYTES
      text.value = await blob.slice(0, MAX_TEXT_BYTES).text()
    } else if (['pdf', 'office-pdf'].includes(kind.value)) {
      // slice() relabels the blob without copying it. A blob: URL is served with the blob's own
      // type, so this is what decides what the frame below becomes: labelled application/pdf it
      // can only resolve to a PDF viewer document, and bytes that are really HTML fail to parse
      // instead of running as a document on our origin. Passing the response blob through
      // unchanged would make that depend on a response header we no longer control at this point.
      objectUrl.value = URL.createObjectURL(blob.slice(0, blob.size, 'application/pdf'))
    } else {
      objectUrl.value = URL.createObjectURL(blob)
    }
  } catch {
    if (current === generation) error.value = 'This file could not be loaded.'
  } finally {
    if (current === generation) loading.value = false
  }
}

watch(() => [props.file?.id, props.file?.previewFileId, props.file?.state, props.file?.previewState, props.sharedToken], load, { immediate: true })
watch(text, () => nextTick(() => { if (markdownEl.value) void renderMermaidBlocks(markdownEl.value) }))
onBeforeUnmount(() => { generation += 1; releaseUrl() })
</script>

<template>
  <div class="preview" :data-preview-kind="kind">
    <div v-if="!file" class="preview-state">Select a file to preview it.</div>
    <div v-else-if="loading" class="preview-state">Loading {{ file.name }}…</div>
    <div v-else-if="error" class="preview-state error">{{ error }}</div>
    <div v-else-if="kind === 'pending'" class="preview-state" data-preview-pending>
      <strong>Preview is being prepared</strong><span>{{ file.name }}</span>
    </div>
    <div v-else-if="kind === 'expired'" class="preview-state" data-preview-expired>
      <strong>This temporary file has expired</strong><span>Ask the agent to create or upload it again.</span>
    </div>
    <img v-else-if="kind === 'image' && objectUrl" :src="objectUrl" :alt="file.name" data-file-preview="image">
    <!-- Deliberately no sandbox attribute. Chrome refuses to instantiate its PDF viewer inside a
         sandboxed frame under every token combination tried, including allow-scripts and
         allow-same-origin, so any sandbox here replaces the document with the grey "blocked
         content" placeholder. The attribute was never what kept an upload off our origin anyway:
         allow-scripts with allow-same-origin would let a blob: frame drop its own sandbox, since
         a blob inherits the creating document's origin. What holds the line is the
         application/pdf label load() puts on the blob — the frame can only become a PDF viewer
         document, which is cross-origin to this app and ignores an /OpenAction or embedded
         /JavaScript in the file. -->
    <iframe v-else-if="['pdf', 'office-pdf'].includes(kind) && objectUrl" :src="objectUrl"
      :title="`Preview of ${file.name}`" data-file-preview="pdf"></iframe>
    <div v-else-if="kind === 'markdown'" class="document markdown" data-file-preview="markdown">
      <div ref="markdownEl" class="md" v-html="renderMarkdown(text)"></div>
      <p v-if="truncated" class="truncated">Preview limited to the first 1 MiB.</p>
    </div>
    <div v-else-if="kind === 'text'" class="document" data-file-preview="text">
      <pre>{{ text }}</pre><p v-if="truncated" class="truncated">Preview limited to the first 1 MiB.</p>
    </div>
    <div v-else-if="officePreviewDisabled" class="preview-state" data-preview-office-disabled>
      <strong>Preview for Office documents is switched off</strong>
      <span>An administrator can enable it for this instance; it converts documents to PDF in a
        separate renderer.</span>
      <a href="https://github.com/open-agenthub/open-agenthub/blob/main/README.md#office-document-previews"
         target="_blank" rel="noopener noreferrer" data-office-preview-docs>How to enable it</a>
      <a v-if="objectUrl" :href="objectUrl" :download="file.name" data-file-download>Download {{ file.name }}</a>
    </div>
    <div v-else class="preview-state" data-preview-unsupported>
      <strong>No safe inline preview is available</strong>
      <a v-if="objectUrl" :href="objectUrl" :download="file.name" data-file-download>Download {{ file.name }}</a>
    </div>
  </div>
</template>

<style scoped>
.preview { height: 100%; min-width: 0; min-height: 0; display: flex; overflow: auto; background: #0c1118; }
.preview > img { max-width: 100%; max-height: 100%; margin: auto; padding: 24px; object-fit: contain; }
.preview > iframe { width: 100%; height: 100%; border: 0; background: white; }
.preview-state { margin: auto; display: flex; max-width: 420px; flex-direction: column; align-items: center; gap: 8px; padding: 28px; color: #8692a2; text-align: center; font-size: 12px; }
.preview-state strong { color: #dce5f1; font-size: 13px; }
.preview-state a { margin-top: 6px; padding: 8px 12px; border: 1px solid #354255; border-radius: 8px; color: var(--accent); text-decoration: none; }
.preview-state.error { color: var(--danger); }
.document { width: 100%; max-width: 860px; margin: 0 auto; padding: 30px 36px 60px; color: #d6deea; }
.document pre { margin: 0; white-space: pre-wrap; word-break: break-word; font: 12.5px/1.65 var(--mono); }
.md :deep(p), .md :deep(ul), .md :deep(ol) { margin: 0 0 10px; }
.md :deep(h3), .md :deep(h4), .md :deep(h5) { margin: 20px 0 10px; color: #f2f6fb; }
.md :deep(code) { font-family: var(--mono); }
.md :deep(pre) { padding: 12px; overflow: auto; border: 1px solid #293544; border-radius: 8px; background: #111923; }
.md :deep(.md-table) { overflow-x: auto; margin: 0 0 10px; }
.md :deep(table) { border-collapse: collapse; font-size: 13px; }
.md :deep(th), .md :deep(td) { border: 1px solid #293544; padding: 6px 12px; text-align: left; vertical-align: top; }
.md :deep(th) { background: #111923; color: #f2f6fb; font-weight: 600; }
.md :deep(blockquote) { margin: 0 0 10px; padding: 2px 14px; border-left: 3px solid #354255; color: #8692a2; }
.md :deep(hr) { border: none; border-top: 1px solid #293544; margin: 16px 0; }
.md :deep(li.task) { list-style: none; margin-left: -18px; }
.md :deep(li.task input) { margin-right: 6px; vertical-align: -1px; accent-color: var(--accent); }
.md :deep(.md-mermaid-svg) { border: 1px solid #293544; border-radius: 8px; background: #111923; padding: 14px; margin: 0 0 10px; overflow-x: auto; display: flex; justify-content: center; }
.md :deep(.md-mermaid-svg svg) { max-width: 100%; height: auto; }
.truncated { margin-top: 18px; color: var(--warn); font-size: 11px; }
</style>
