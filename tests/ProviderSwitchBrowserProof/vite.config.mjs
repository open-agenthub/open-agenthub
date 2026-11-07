import { fileURLToPath } from 'node:url'
import base from '../../frontend/vite.config.js'
export default ({ mode }) => ({ ...base({ mode }), root: fileURLToPath(new URL('.', import.meta.url)),
  resolve: { alias: { '@provider-switch': fileURLToPath(new URL('../../frontend/src/components/workspace/ProviderSwitch.vue', import.meta.url)), vue: fileURLToPath(new URL('../../frontend/node_modules/vue/dist/vue.esm-bundler.js', import.meta.url)) } },
  server: { host: '0.0.0.0', port: 5173, strictPort: true, fs: { allow: [fileURLToPath(new URL('../..', import.meta.url))] } } })
