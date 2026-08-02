<script setup>
import { computed } from 'vue'
import { fileIcon, fileSize } from '../lib/files.js'
import FilePreview from './FilePreview.vue'

const props = defineProps({
  sessionId: { type: String, required: true },
  files: { type: Array, default: () => [] },
  selectedId: { type: String, default: null },
  capabilities: { type: Object, default: () => ({}) },
  canWrite: { type: Boolean, default: false },
  sharedToken: { type: String, default: null },
  presented: { type: Boolean, default: false }
})
defineEmits(['select', 'close', 'dismiss'])
const selected = computed(() => props.files.find(file => file.id === props.selectedId) || null)
</script>

<template>
  <section class="files-pane" data-files-pane>
    <aside class="file-rail">
      <header><strong>Files</strong><span>{{ files.length }}</span></header>
      <div v-if="!files.length" class="empty">Images and documents created by the agent appear here.</div>
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
