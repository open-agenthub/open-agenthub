<script setup>
import { ref } from 'vue'
import { signInWithApiKey } from '../auth.js'

// The key prompt for installs without OIDC. A real form with a password field, so password
// managers can fill it and the key never shows on screen.
const props = defineProps({ signIn: { type: Function, default: signInWithApiKey } })
const emit = defineEmits(['signed-in'])
const key = ref('')
const error = ref('')
const busy = ref(false)

async function submit() {
  if (busy.value) return
  busy.value = true
  error.value = ''
  try {
    await props.signIn(key.value)
    key.value = ''
    emit('signed-in')
  } catch (e) {
    error.value = String(e?.message || e)
  } finally {
    busy.value = false
  }
}
</script>
<template>
  <div class="login" data-api-key-login>
    <form class="login-card" @submit.prevent="submit">
      <h1>Open AgentHub</h1>
      <p>This instance is protected by an API key. Enter it to manage your agent sessions; it is kept for this browser session only.</p>
      <input v-model="key" type="password" name="password" autocomplete="current-password" placeholder="API key"
        aria-label="API key" required autofocus :disabled="busy" data-api-key-input />
      <p v-if="error" class="err" role="alert" data-api-key-error>{{ error }}</p>
      <button class="primary" type="submit" :disabled="busy || !key.trim()">Sign in</button>
    </form>
  </div>
</template>
<style scoped>
.login { height: 100%; display: flex; align-items: center; justify-content: center; }
.login-card { max-width: 380px; padding: 30px; border: 1px solid var(--border-2); border-radius: var(--radius-lg); background: var(--panel); text-align: center; display: flex; flex-direction: column; gap: 14px; }
.login-card h1 { font-size: 24px; margin: 0; }
.login-card p { color: var(--muted-2); margin: 0; }
.login-card .err { color: var(--danger); font-weight: 600; }
.login-card input { width: 100%; box-sizing: border-box; }
</style>
