<script setup>
import { ref } from 'vue'

// The agent CLI is showing one of its own screens (#112): a menu is offered as buttons that send
// the keys a person would press in the terminal; a prompt for typed input points at the
// terminal drawer, which the viewer can open when needed. An entry that takes typed text instead
// of being picked ("Type something.", #250) is a text field whose content is sent as the answer.
// A multi-select list ticks its options one by one and moves on with its own step button
// ("Submit", "Next"); OpenCode then shows a review of the answers to submit (#284).
defineProps({ menu: Object, canAnswer: Boolean })
const emit = defineEmits(['choose', 'answer', 'step', 'confirm', 'cancel', 'open-terminal'])
const drafts = ref({})

function submit(number) {
  const text = drafts.value[number]?.trim()
  if (!text) return
  emit('answer', number, text)
  drafts.value[number] = ''
}
// Enter sends, as in the composer; Shift+Enter keeps a line break, which the CLI's field takes.
function onKeydown(event, number) {
  if (event.key !== 'Enter' || event.shiftKey || event.isComposing) return
  event.preventDefault()
  submit(number)
}
</script>
<template>
  <section class="cli-menu" role="status" aria-live="polite" data-cli-menu>
    <p class="cli-menu-head">
      <span class="dot" aria-hidden="true"></span>
      <strong>{{ menu.kind === 'choice' ? 'The agent is showing a menu' : menu.kind === 'confirm' ? 'The agent is waiting for you to submit' : 'The agent is waiting for input' }}</strong>
    </p>
    <p v-if="menu.title" class="cli-menu-title" data-cli-title>{{ menu.title }}</p>
    <div v-if="menu.kind === 'choice' && canAnswer" class="cli-menu-options">
      <template v-for="option in menu.options" :key="option.number">
        <form v-if="option.freeText" class="free-text" :class="{ current: option.selected }" :data-cli-free-text="option.number"
          @submit.prevent="submit(option.number)">
          <span class="number">{{ option.number }}</span>
          <textarea v-model="drafts[option.number]" rows="1" :placeholder="option.label.replace(/^\[[ ✔✓]\]\s*/, '').replace(/ · .*$/, '')"
            :aria-label="`Answer in your own words (option ${option.number})`" @keydown="onKeydown($event, option.number)"></textarea>
          <button type="submit" class="primary" :disabled="!drafts[option.number]?.trim()">Send</button>
        </form>
        <button v-else type="button" :class="{ current: option.selected }"
          :data-cli-option="option.number" @click="$emit('choose', option.number)">
          <span class="number">{{ option.number }}</span>{{ option.label }}
        </button>
      </template>
      <button v-if="menu.step" type="button" class="step primary" :class="{ current: menu.step.selected }" data-cli-step
        @click="$emit('step')">{{ menu.step.label }} →</button>
    </div>
    <div v-if="menu.kind === 'confirm'" class="cli-menu-options" data-cli-confirm>
      <p v-for="(line, index) in menu.details || []" :key="index" class="detail">{{ line }}</p>
      <button v-if="canAnswer" type="button" class="step primary" data-cli-submit @click="$emit('confirm')">Submit answers</button>
    </div>
    <p class="cli-menu-hint">
      {{ menu.kind === 'confirm' ? 'Submit here or in the agent terminal.'
        : menu.kind !== 'choice' ? 'Type the answer in the agent terminal.'
          : menu.partial ? 'The terminal shows only part of the list; the rest is in the agent terminal.'
            : menu.step ? `Tick what applies, then ${menu.step.label}.` : 'Choose here or in the agent terminal.' }}
      <button type="button" class="link" data-cli-open-terminal @click="$emit('open-terminal')">Open terminal</button>
      <button v-if="canAnswer" type="button" class="link" data-cli-cancel @click="$emit('cancel')">Cancel (Esc)</button>
    </p>
  </section>
</template>
<style scoped>
.cli-menu { margin: 0 auto 8px; width: calc(100% - 64px); max-width: 840px; padding: 12px 14px; border: 1px solid var(--warn); border-radius: 14px; background: var(--panel); font-size: 12px; box-sizing: border-box; }
.cli-menu-head { display: flex; align-items: center; gap: 6px; margin: 0 0 6px; flex-wrap: wrap; }
.dot { width: 7px; height: 7px; border-radius: 50%; background: var(--warn); }
/* The whole question, wrapped: a long one scrolls inside the notice rather than being cut. */
.cli-menu-title { margin: 0 0 8px; color: var(--text); white-space: pre-wrap; overflow-wrap: anywhere; max-height: 30vh; overflow-y: auto; line-height: 1.45; }
.cli-menu-options { display: flex; flex-direction: column; gap: 4px; }
.cli-menu-options button { display: flex; gap: 8px; text-align: left; padding: 7px 10px; border-radius: 9px; font-size: 12px; overflow-wrap: anywhere; }
.cli-menu-options button.current, .free-text.current { border-color: var(--accent); }
.free-text { display: flex; align-items: flex-start; gap: 8px; padding: 4px 4px 4px 10px; border: 1px solid var(--border); border-radius: 9px; }
.free-text .number { padding-top: 5px; }
.cli-menu-options button.step { align-self: flex-start; justify-content: center; }
.detail { margin: 0; white-space: pre-wrap; overflow-wrap: anywhere; }
.free-text textarea { flex: 1; min-width: 0; min-height: 26px; resize: vertical; font: inherit; font-size: 12px; padding: 4px 6px; box-sizing: border-box; }
.free-text button { flex: none; justify-content: center; }
.number { color: var(--muted-3); font-variant-numeric: tabular-nums; }
.cli-menu-hint { margin: 8px 0 0; color: var(--muted-3); font-size: 11px; }
.link { border: 0; background: none; padding: 0 4px; color: var(--accent); font-size: 11px; text-decoration: underline; }
@media(max-width: 640px) { .cli-menu { width: calc(100% - 20px); } }
</style>
