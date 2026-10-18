<script setup>
// The agent CLI is showing one of its own screens (#112): a menu is offered as buttons that send
// the keys a person would press in the terminal; a prompt for typed input points at the
// terminal drawer, which the viewer can open when needed.
defineProps({ menu: Object, canAnswer: Boolean })
defineEmits(['choose', 'cancel', 'open-terminal'])
</script>
<template>
  <section class="cli-menu" role="status" aria-live="polite" data-cli-menu>
    <p class="cli-menu-head">
      <span class="dot" aria-hidden="true"></span>
      <strong>{{ menu.kind === 'choice' ? 'The agent is showing a menu' : 'The agent is waiting for input' }}</strong>
      <span v-if="menu.title" class="cli-menu-title">· {{ menu.title }}</span>
    </p>
    <div v-if="menu.kind === 'choice' && canAnswer" class="cli-menu-options">
      <button v-for="option in menu.options" :key="option.number" type="button" :class="{ current: option.selected }"
        :data-cli-option="option.number" @click="$emit('choose', option.number)">
        <span class="number">{{ option.number }}</span>{{ option.label }}
      </button>
    </div>
    <p class="cli-menu-hint">
      {{ menu.kind !== 'choice' ? 'Type the answer in the agent terminal.'
        : menu.partial ? 'The terminal shows only part of the list; the rest is in the agent terminal.'
          : 'Choose here or in the agent terminal.' }}
      <button type="button" class="link" data-cli-open-terminal @click="$emit('open-terminal')">Open terminal</button>
      <button v-if="canAnswer" type="button" class="link" data-cli-cancel @click="$emit('cancel')">Cancel (Esc)</button>
    </p>
  </section>
</template>
<style scoped>
.cli-menu { margin: 0 auto 8px; width: calc(100% - 64px); max-width: 840px; padding: 12px 14px; border: 1px solid var(--warn); border-radius: 14px; background: var(--panel); font-size: 12px; box-sizing: border-box; }
.cli-menu-head { display: flex; align-items: center; gap: 6px; margin: 0 0 8px; flex-wrap: wrap; }
.dot { width: 7px; height: 7px; border-radius: 50%; background: var(--warn); }
.cli-menu-title { color: var(--muted); }
.cli-menu-options { display: flex; flex-direction: column; gap: 4px; }
.cli-menu-options button { display: flex; gap: 8px; text-align: left; padding: 7px 10px; border-radius: 9px; font-size: 12px; overflow-wrap: anywhere; }
.cli-menu-options button.current { border-color: var(--accent); }
.number { color: var(--muted-3); font-variant-numeric: tabular-nums; }
.cli-menu-hint { margin: 8px 0 0; color: var(--muted-3); font-size: 11px; }
.link { border: 0; background: none; padding: 0 4px; color: var(--accent); font-size: 11px; text-decoration: underline; }
@media(max-width: 640px) { .cli-menu { width: calc(100% - 20px); } }
</style>
