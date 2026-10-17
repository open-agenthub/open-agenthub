<script setup>
import { ref } from 'vue'
import { preferredUi, savePreferredUi, UI_OPTIONS } from '../lib/ui-preference.js'

const selected = ref(preferredUi())
const saved = ref(false)
const error = ref('')
function save() {
  saved.value = false
  error.value = ''
  try { savePreferredUi(selected.value); saved.value = true }
  catch { error.value = 'Could not save the preference. Allow browser storage and try again.' }
}
</script>
<template>
  <div class="interface-settings">
    <h3>Interface</h3>
    <p>Choose the default view for your sessions and the New session dialog. Saved in this browser.</p>
    <form @submit.prevent="save">
      <fieldset>
        <legend>Preferred interface</legend>
        <label v-for="option in UI_OPTIONS" :key="option.key">
          <input v-model="selected" type="radio" name="preferred-ui" :value="option.key" @change="saved = false">
          <span><strong>{{ option.label }}</strong><small>{{ option.hint }}</small></span>
        </label>
      </fieldset>
      <button type="submit" class="primary">Save preference</button>
      <p v-if="saved" role="status">Preference saved.</p>
      <p v-if="error" role="alert">{{ error }}</p>
    </form>
  </div>
</template>
<style scoped>
.interface-settings { max-width: 560px; padding: 26px 30px; }
h3 { font-size: 22px; margin: 0 0 16px; }
p { color: var(--muted-2); font-size: 13px; line-height: 1.6; }
fieldset { border: 1px solid var(--border-2); border-radius: var(--radius); margin: 20px 0; padding: 16px; }
legend { padding: 0 6px; font-size: 13px; }
label { display: flex; align-items: center; gap: 12px; padding: 12px 0; cursor: pointer; }
input[type=radio] { width: auto; margin: 0; flex: 0 0 auto; accent-color: var(--accent); }
small { display: block; margin-top: 4px; color: var(--muted-3); }
[role=alert] { color: var(--danger); }
</style>
