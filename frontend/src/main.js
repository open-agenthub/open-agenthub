import { createApp } from 'vue'
import App from './App.vue'
import { initAuth } from './api.js'
import '@xterm/xterm/css/xterm.css'
import './style.css'

// Initialize OIDC first (runtime config + possible callback), then mount the app.
initAuth().then(() => createApp(App).mount('#app'))
