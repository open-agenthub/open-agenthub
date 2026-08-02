<script setup>
defineProps({
  items: { type: Array, default: () => [] },
  retry: { type: Function, required: true },
  remove: { type: Function, required: true },
  transcript: { type: Boolean, default: false }
})

function sizeLabel(bytes) {
  if (!Number.isFinite(bytes)) return ''
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${Math.ceil(bytes / 1024)} KiB`
  return `${(bytes / 1024 / 1024).toFixed(1)} MiB`
}
</script>

<template>
  <div class="attachments" :class="{ transcript }" data-chat-attachments>
    <article v-for="item in items" :key="item.key || item.id" class="attachment" :class="`is-${item.state || 'ready'}`"
      :data-attachment="item.key || item.id" :data-attachment-id="item.id">
      <div class="file-mark" aria-hidden="true">{{ item.mimeType?.startsWith('image/') ? 'IMG' : 'FILE' }}</div>
      <div class="file-copy">
        <strong>{{ item.name }}</strong>
        <span v-if="item.state === 'uploading'">Uploading · {{ item.progress }}%</span>
        <span v-else-if="item.state === 'ready' || transcript">{{ sizeLabel(item.size) }}</span>
        <span v-else-if="item.state === 'queued'">Waiting…</span>
        <span v-if="item.error" class="attachment-error" data-attachment-error>{{ item.error }}</span>
        <span v-if="item.state === 'uploading'" class="progress" aria-hidden="true"><i :style="{ width: `${item.progress}%` }"></i></span>
      </div>
      <button v-if="item.state === 'failed' && !item.validationError && !transcript" type="button" class="text-action"
        data-attachment-retry @click="retry(item.key)">Retry</button>
      <button v-if="!transcript" type="button" class="remove" :data-attachment-remove="item.key"
        :aria-label="`Remove ${item.name}`" @click="remove(item.key)">×</button>
    </article>
  </div>
</template>

<style scoped>
.attachments { display: flex; flex-wrap: wrap; gap: 8px; min-width: 0; }
.attachment { position: relative; display: grid; grid-template-columns: 34px minmax(90px, 1fr) auto auto; align-items: center; gap: 9px; min-width: 190px; max-width: 320px; padding: 8px 9px; background: var(--panel-2); border: 1px solid var(--border-2); border-radius: var(--radius); }
.attachment.is-failed { border-color: color-mix(in srgb, var(--danger) 55%, var(--border)); }
.file-mark { display: grid; place-items: center; width: 34px; height: 34px; border: 1px solid var(--border-2); border-radius: 7px; color: var(--accent); background: var(--input); font: 700 9px/1 var(--mono); letter-spacing: .05em; }
.file-copy { min-width: 0; display: flex; flex-direction: column; gap: 2px; }
.file-copy strong { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; color: var(--text); font-size: 12px; }
.file-copy span { color: var(--muted-2); font-size: 10px; }
.file-copy .attachment-error { color: var(--danger); white-space: normal; }
.progress { height: 2px; overflow: hidden; margin-top: 2px; border-radius: 2px; background: var(--border-2); }
.progress i { display: block; height: 100%; background: var(--accent); transition: width 160ms ease; }
.remove, .text-action { align-self: center; padding: 3px 6px; border: 0; color: var(--muted-2); background: transparent; }
.remove:hover, .text-action:hover { color: var(--text); background: var(--hover); }
.text-action { color: var(--accent); font-size: 11px; }
.transcript { margin-top: 8px; }
.transcript .attachment { min-width: 170px; background: var(--input); }
@media (prefers-reduced-motion: reduce) { .progress i { transition: none; } }
</style>
