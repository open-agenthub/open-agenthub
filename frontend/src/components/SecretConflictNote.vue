<script setup>
// Names in the secret store are not unique (#253); a session receiving two secrets that land in
// the same place is. The dialogs show the clash before saving, so the owner is not left to read
// it out of the hub's refusal after pressing the button.
import { computed } from 'vue'
import { secretConflicts, secretPlace } from '../lib/secrets.js'

const props = defineProps({
  // The secrets the session (or project) would receive with the current choices.
  secrets: { type: Array, default: () => [] },
  subject: { type: String, default: 'This session' }
})
const conflicts = computed(() => secretConflicts(props.secrets))
const label = secret => secret.description ? `${secret.description} (${secret.id})` : secret.id
</script>

<template>
  <div v-if="conflicts.length" class="conflict" role="alert" data-secret-conflict>
    <p v-for="c in conflicts" :key="`${c.delivery}:${c.name}`">
      {{ subject }} would receive {{ c.secrets.length }} secrets as <code>{{ secretPlace(c) }}</code>:
      {{ c.secrets.map(label).join(', ') }}. Only one value can be there — leave all but one of them out,
      or rename one under Settings → Secrets.
    </p>
  </div>
</template>

<style scoped>
.conflict { margin-top: 10px; padding: 8px 10px; border: 1px solid var(--danger); border-radius: 8px; color: var(--danger); font-size: 12px; line-height: 1.5; }
.conflict p { margin: 0; }
.conflict p + p { margin-top: 6px; }
code { font-family: var(--mono); font-size: 11px; }
</style>
