<script setup>
defineProps({ name: { type: String, required: true } })
</script>
<template>
  <svg class="session-icon" :data-session-icon="name" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true" focusable="false">
    <template v-if="name === 'expiry'">
      <path d="M6 3h12M6 21h12M7 3v4c0 2 2 3 5 5-3 2-5 3-5 5v4M17 3v4c0 2-2 3-5 5 3 2 5 3 5 5v4" />
    </template>
    <template v-else-if="name === 'duplicate'">
      <rect x="8" y="8" width="12" height="13" rx="2" />
      <path d="M16 8V5a2 2 0 0 0-2-2H5a2 2 0 0 0-2 2v9a2 2 0 0 0 2 2h3" />
    </template>
  </svg>
</template>
<style scoped>
.session-icon { width: 1em; height: 1em; display: inline-block; vertical-align: -0.15em; flex-shrink: 0; }
</style>
