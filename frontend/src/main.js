import { createApp } from 'vue'
import App from './App.vue'
import { initAuth } from './api.js'
import '@xterm/xterm/css/xterm.css'
import './fonts.js'
import './style.css'

// Initialize OIDC first (runtime config + possible callback), then mount the app.
initAuth().then(async () => {
  const root = location.pathname === '/browser-popout'
    ? (await import('./components/BrowserPopout.vue')).default : App
  createApp(root).mount('#app')
})
