<script setup>
// Vue port of the compact WorkLog row geometry; provenance and license in public/licenses/t3code.txt.
defineProps({ label: String, state: String, failed: Boolean })
</script>

<template>
  <details class="work-log" :class="{ failed }" data-work-log>
    <summary>
      <span class="work-icon" aria-hidden="true">›</span>
      <span class="work-label">{{ label }}</span>
      <span v-if="state" class="work-state">{{ state }}</span>
    </summary>
    <div class="work-details"><slot /></div>
  </details>
</template>

<style scoped>
.work-log { min-width: 0; padding: 2px 0; border-radius: 6px; }
summary { display: flex; align-items: center; min-height: 24px; min-width: 0; gap: 6px; padding: 2px; cursor: pointer; color: var(--muted); font-size: 13px; line-height: 1.6; list-style: none; }
summary::-webkit-details-marker { display: none; }
summary:hover { color: var(--strong); background: var(--hover); border-radius: 6px; }
summary:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
.work-icon { display: flex; width: 24px; height: 24px; flex-shrink: 0; justify-content: center; align-items: center; font-size: 20px; }
details[open] .work-icon { transform: rotate(90deg); }
.work-label { min-width: 0; flex: 1; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.work-state { flex-shrink: 0; color: var(--muted-3); font-size: 11px; }
.failed .work-state { color: var(--danger); }
.work-details { margin-inline-start: 28px; display: flex; flex-direction: column; gap: 12px; max-height: 384px; overflow: auto; padding: 4px 2px; }
.work-details :deep(pre) { margin: 0; white-space: pre-wrap; overflow-wrap: anywhere; font: 12px/1.6 var(--mono); color: var(--muted); }
</style>
