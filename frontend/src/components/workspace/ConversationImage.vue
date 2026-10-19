<script setup>
import { onBeforeUnmount, ref, watch } from 'vue'
import { getSessionFileContent, getSharedSessionFileContent } from '../../api.js'

const props = defineProps({ sessionId: String, sharedToken: String, file: { type: Object, required: true } })
const url = ref('')
const error = ref('')
const dialog = ref(null)
let generation = 0
function release() {
  dialog.value?.close()
  if (url.value) URL.revokeObjectURL(url.value)
  url.value = ''
}
watch(() => [props.sessionId, props.sharedToken, props.file.id].join('|'), async () => {
  const current = ++generation
  release()
  error.value = ''
  try {
    // Reuse Files' authorized fetch: an img URL alone cannot carry a signed-in user's token.
    const blob = props.sharedToken
      ? await getSharedSessionFileContent(props.sharedToken, props.file.id)
      : await getSessionFileContent(props.sessionId, props.file.id)
    if (current !== generation) return
    if (!['image/png', 'image/jpeg', 'image/webp', 'image/gif'].includes(blob.type)) throw new Error('unsupported_image')
    url.value = URL.createObjectURL(blob)
  } catch {
    if (current === generation) error.value = 'Image unavailable'
  }
}, { immediate: true })
onBeforeUnmount(() => { generation += 1; release() })
</script>

<template>
  <div class="conversation-image" :data-conversation-image="file.id">
    <button v-if="url && !error" type="button" class="thumbnail" :aria-label="`Enlarge ${file.name}`" @click="dialog.showModal()">
      <img :src="url" :alt="file.name" @error="error = 'Image unavailable'">
    </button>
    <span v-if="error" class="image-state" role="status">{{ error }} · {{ file.name }}</span>
    <span v-else-if="!url" class="image-state">Loading image…</span>
    <dialog ref="dialog" class="image-dialog" :aria-label="file.name" @click="($event.target === dialog) && dialog.close()">
      <button type="button" class="image-close" aria-label="Close image" @click="dialog.close()">✕</button>
      <img v-if="url && !error" :src="url" :alt="file.name">
    </dialog>
  </div>
</template>

<style scoped>
.conversation-image { margin-top: 8px; }
.thumbnail { display: block; padding: 0; overflow: hidden; border: 1px solid var(--border-2); border-radius: 8px; background: var(--input); cursor: zoom-in; }
.thumbnail img { display: block; max-width: min(280px, 100%); max-height: 180px; object-fit: contain; }
.image-state { color: var(--muted-3); font-size: 12px; }
.image-dialog { max-width: 94vw; max-height: 94vh; padding: 32px 12px 12px; border: 1px solid var(--border-2); border-radius: 10px; background: var(--panel); }
.image-dialog::backdrop { background: #000b; }
.image-dialog img { display: block; max-width: 88vw; max-height: 82vh; object-fit: contain; }
.image-close { position: absolute; top: 4px; right: 6px; border: 0; background: transparent; color: var(--text); }
</style>
