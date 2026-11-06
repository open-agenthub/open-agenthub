<script setup>
// The recipient field of the share dialogs (#470): a combobox over the lookup's suggestions.
// Picking a suggestion selects it and does not share yet — the dialog's Add button does, with the
// role chosen next to it (docs/session-sharing.md, "Choosing a recipient").
//
// The typed text and the selection are kept apart on purpose. The dialogs used to write the
// chosen username into the input, which is also what starts a lookup: when the text already was
// that username nothing changed, otherwise a fresh lookup showed the same suggestion again — the
// click had no visible effect either way. Here only typing changes `query`, so a selection never
// starts a lookup, and the input shows the selected person instead of the text.
import { computed, ref, watch } from 'vue'

const props = defineProps({
  query: { type: String, default: '' },
  // [{ value, label, detail? }] — value is what Add sends (username or group name).
  options: { type: Array, default: () => [] },
  selected: { type: Object, default: null },
  inputLabel: { type: String, required: true },
  listLabel: { type: String, required: true },
  placeholder: { type: String, default: '' },
  describedby: { type: String, default: undefined },
  // Groups are offered before anything is typed; user suggestions only answer typed text.
  openOnFocus: { type: Boolean, default: false }
})
defineOptions({ inheritAttrs: false })
const emit = defineEmits(['update:query', 'update:selected', 'submit'])

const uid = `recipient-${Math.random().toString(36).slice(2, 10)}`
const listId = `${uid}-list`
const open = ref(false)
const active = ref(-1)

const shown = computed(() => open.value && props.options.length > 0)
const text = computed(() => props.selected ? display(props.selected) : props.query)
// The name alone can be ambiguous (two people called Alex), so the username follows it unless
// the two are the same.
function display(option) { return option.detail && option.label !== option.value ? `${option.label} (${option.value})` : option.label }
function optionId(index) { return `${uid}-opt-${index}` }

// New suggestions arrive while typing; the highlight must not point past the end of the list or
// at a different person than before.
watch(() => props.options, () => { active.value = -1 })

function onInput(event) {
  if (props.selected) emit('update:selected', null)
  emit('update:query', event.target.value)
  open.value = true
}
function choose(option) {
  emit('update:selected', option)
  open.value = false
  active.value = -1
}
function move(step) {
  if (!props.options.length) return
  if (!open.value) { open.value = true; active.value = step > 0 ? 0 : props.options.length - 1; return }
  const count = props.options.length
  active.value = active.value < 0 ? (step > 0 ? 0 : count - 1) : (active.value + step + count) % count
}
function onKeydown(event) {
  if (event.key === 'ArrowDown') { event.preventDefault(); move(1) }
  else if (event.key === 'ArrowUp') { event.preventDefault(); move(-1) }
  else if (event.key === 'Enter') {
    event.preventDefault()
    // Enter on a highlighted suggestion selects it; a second Enter shares, like the Add button.
    if (shown.value && active.value >= 0) choose(props.options[active.value])
    else emit('submit')
  } else if (event.key === 'Escape' && shown.value) {
    // Only the list closes; the dialog stays open, so the event goes no further.
    event.preventDefault(); event.stopPropagation()
    open.value = false; active.value = -1
  }
}
function onFocus() { if (props.openOnFocus && !props.selected) open.value = true }
function onBlur() { open.value = false; active.value = -1 }
</script>

<template>
  <div class="picker">
    <input :value="text" role="combobox" :aria-label="inputLabel" :placeholder="placeholder" autocomplete="off"
      aria-autocomplete="list" :aria-expanded="shown ? 'true' : 'false'" :aria-controls="listId"
      :aria-activedescendant="shown && active >= 0 ? optionId(active) : undefined" :aria-describedby="describedby"
      :class="{ chosen: !!selected }" :data-recipient-selected="selected ? selected.value : undefined"
      v-bind="$attrs" @input="onInput" @keydown="onKeydown" @focus="onFocus" @blur="onBlur" />
    <span v-if="selected" class="tick" aria-hidden="true">✓</span>
    <!-- mousedown is cancelled so the input keeps focus and its blur does not close the list
         before the click lands. -->
    <ul v-show="shown" :id="listId" role="listbox" :aria-label="listLabel" class="options">
      <li v-for="(option, index) in options" :id="optionId(index)" :key="option.value" role="option"
        :aria-selected="index === active ? 'true' : 'false'" :class="{ active: index === active }"
        data-recipient-option @mousedown.prevent @mousemove="active = index" @click="choose(option)">
        <span class="label">{{ option.label }}</span><span v-if="option.detail" class="detail">{{ option.detail }}</span>
      </li>
    </ul>
  </div>
</template>

<style scoped>
.picker { position: relative; flex: 1 1 160px; min-width: 0; }
.picker input { width: 100%; }
.picker input.chosen { border-color: var(--accent); font-weight: 600; padding-right: 28px; }
.tick { position: absolute; right: 10px; top: 50%; transform: translateY(-50%); color: var(--accent); font-weight: 700; pointer-events: none; }
.options {
  position: absolute; z-index: 5; left: 0; right: 0; top: calc(100% + 4px);
  list-style: none; margin: 0; padding: 4px 0; max-height: 220px; overflow-y: auto;
  border: 1px solid var(--border-2); border-radius: 10px; background: var(--panel);
  box-shadow: 0 6px 18px rgba(0,0,0,.35);
}
.options li { display: flex; align-items: baseline; gap: 10px; padding: 7px 12px; cursor: pointer; font-size: 13px; color: var(--text); }
.options li.active { background: var(--hover); box-shadow: inset 3px 0 0 var(--accent); }
.detail { font-size: 11px; color: var(--muted-3); font-family: var(--mono); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
</style>
