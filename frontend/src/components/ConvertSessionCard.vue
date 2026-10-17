<script setup>
import { computed, ref, watch } from 'vue'
import { api } from '../api.js'
import { conversionErrorText, conversionHint } from '../lib/conversion.js'
import { preferredUi } from '../lib/ui-preference.js'

// Inline card offering to continue a finished autonomous run interactively. One component for
// the session page and the sessions list, so the two cannot drift on what they send.
const props = defineProps({
  session: { type: Object, required: true },
  // In a list row the card is folded out on demand and can be closed again.
  dismissible: { type: Boolean, default: false }
})
const emit = defineEmits(['converted', 'cancel'])

// Off by default: the autonomous run approved everything because nobody was there to ask.
const keepAutoApprove = ref(false)
const busy = ref('')
const error = ref('')
const hint = computed(() => conversionHint(props.session?.agent))
// The saved interface preference decides which button is the primary one.
const preferred = preferredUi()
const finished = computed(() => props.session?.phase === 'Paused' ? 'is paused' : 'has finished')

watch(() => props.session?.id, () => { keepAutoApprove.value = false; busy.value = ''; error.value = '' })

async function convert(surface) {
  if (busy.value) return
  busy.value = surface
  error.value = ''
  try {
    // Always the PTY, whichever surface was picked: a headless chat transport would leave the
    // converted run without a terminal, /login or the CLI's menus (#114). The surface only
    // selects the first view (docs/session-mode-conversion.md).
    await api.convertSession(props.session.id, {
      mode: 'interactive', uiMode: 'terminal', autoApprove: keepAutoApprove.value
    })
    emit('converted', props.session.id, surface)
  } catch (e) {
    // Shown where the buttons are, never as a popup: a 409 here means "pause or wait first"
    // and the person should read it next to the thing they clicked.
    error.value = conversionErrorText(e)
  } finally {
    busy.value = ''
  }
}
</script>
<template>
  <div class="convert" data-convert-card>
    <span class="dot"></span>
    <div class="text">
      <strong>This autonomous run {{ finished }}. Continue it interactively?</strong>
      <span class="hint" data-convert-hint>{{ hint }}</span>
      <span v-if="error" class="error" data-convert-error>{{ error }}</span>
    </div>
    <label class="keep">
      <input type="checkbox" v-model="keepAutoApprove" :disabled="!!busy" data-convert-keep-auto-approve />
      Keep auto-approve
    </label>
    <div class="actions">
      <button class="bar-btn" :class="{ primary: preferred === 'workspace' }" data-convert-workspace :disabled="!!busy" @click="convert('workspace')">
        {{ busy === 'workspace' ? 'Starting…' : 'Open workspace' }}
      </button>
      <button class="bar-btn" :class="{ primary: preferred === 'terminal' }" data-convert-terminal :disabled="!!busy" @click="convert('terminal')">
        {{ busy === 'terminal' ? 'Starting…' : 'Open terminal' }}
      </button>
      <button v-if="dismissible" class="bar-btn" data-convert-cancel :disabled="!!busy" @click="$emit('cancel')">Not now</button>
    </div>
  </div>
</template>
<style scoped>
.convert { display: flex; align-items: center; gap: 12px; padding: 10px 20px; font-size: 12px; color: var(--accent-2); background: #121a24; border-bottom: 1px solid #24405c; flex-wrap: wrap; }
.dot { width: 7px; height: 7px; border-radius: 50%; background: var(--accent-2); flex-shrink: 0; }
.text { display: flex; flex-direction: column; gap: 2px; min-width: 0; flex: 1; }
.hint { color: var(--muted-3); }
.error { color: var(--danger); }
.keep { display: flex; align-items: center; gap: 6px; margin: 0; color: var(--muted); font-size: 12px; white-space: nowrap; }
.keep input { width: auto; margin: 0; }
.actions { display: flex; gap: 8px; }
.bar-btn { font-size: 12px; padding: 6px 14px; border-radius: 9px; white-space: nowrap; }
</style>
