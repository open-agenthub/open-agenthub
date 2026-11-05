import { defineConfig } from '../../frontend/node_modules/vite/dist/node/index.js'
import vue from '../../frontend/node_modules/@vitejs/plugin-vue/dist/index.mjs'
import { fileURLToPath } from 'node:url'
export default defineConfig({
  root: fileURLToPath(new URL('../../', import.meta.url)),
  plugins: [vue()],
  resolve: { alias: { vue: fileURLToPath(new URL('../../frontend/node_modules/vue/dist/vue.runtime.esm-bundler.js', import.meta.url)) } },
  optimizeDeps: { entries: ['tests/McpLiteralWarningProof/index.html'] },
  server: { host: '0.0.0.0', port: 5173, strictPort: true }
})
