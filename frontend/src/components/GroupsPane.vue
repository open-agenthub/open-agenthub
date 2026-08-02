<script setup>
import { ref, onMounted } from 'vue'
import { api } from '../api.js'

// Admin pane: instance-wide "users may publish skills to everyone" switch.
// IdP groups (via Admin → Usage limits & groups) are used for share targets.
// Everything here is enterprise — a 402 renders one locked card.
const loading = ref(true)
const locked = ref(false)
const error = ref('')

const publishing = ref(false)
const pubMsg = ref('')

async function load() {
  loading.value = true; error.value = ''
  try {
    const settings = await api.librarySettings()
    publishing.value = !!settings.userSkillPublishing
  } catch (e) {
    if (e.status === 402) locked.value = true
    else error.value = String(e.message || e)
  } finally {
    loading.value = false
  }
}
onMounted(load)

async function togglePublishing(e) {
  const on = e.target.checked
  pubMsg.value = ''
  try {
    await api.setLibrarySettings({ userSkillPublishing: on })
    publishing.value = on
    pubMsg.value = 'Saved ✓'
  } catch (err) {
    e.target.checked = !on
    publishing.value = !on
    if (err.status === 402) locked.value = true
    else error.value = String(err.message || err)
  }
}
</script>

<template>
  <div class="embed">
    <div class="embed-inner">
      <h3 class="pane-head">Skill publishing</h3>
      <p v-if="loading" class="muted">Loading…</p>

      <section v-else-if="locked" class="card cta" data-library-locked>
        <div class="card-head">
          <h4>Library sharing is an enterprise feature</h4>
          <p class="muted">Activate a license to let users publish skills and share catalog entries with IdP groups.</p>
        </div>
      </section>

      <template v-else>
        <p v-if="error" class="err">{{ error }}</p>
        <section class="card">
          <div class="card-head">
            <h4>User skill publishing</h4>
            <p class="muted">When enabled, non-admin users can publish their own skills to everyone. Admins can always share with users or IdP groups.</p>
          </div>
          <label class="check">
            <input type="checkbox" :checked="publishing" data-skill-publishing-toggle @change="togglePublishing" />
            <span>Allow users to publish skills to everyone</span>
          </label>
          <p v-if="pubMsg" class="ok-text" data-publishing-msg>{{ pubMsg }}</p>
        </section>
      </template>
    </div>
  </div>
</template>

<style scoped>
.embed { flex: 1; overflow-y: auto; }
.embed-inner { max-width: 640px; padding: 26px 30px; }
.pane-head { font-size: 22px; margin: 0 0 16px; }
.card { background: var(--panel); border: 1px solid var(--border); border-radius: 12px; padding: 16px 18px; margin-bottom: 14px; }
.card-head h4 { margin: 0 0 6px; font-size: 15px; }
.card.cta { border-style: dashed; }
.check { display: flex; align-items: center; gap: 8px; font-size: 13px; cursor: pointer; margin-top: 10px; }
.check input { width: auto; }
.muted { color: var(--muted); font-size: 13px; margin: 0; }
.err { color: var(--danger); font-family: var(--mono); font-size: 12px; }
.ok-text { color: var(--ok); font-size: 12px; margin: 8px 0 0; }
</style>
