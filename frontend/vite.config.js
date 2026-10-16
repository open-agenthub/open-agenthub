import { fileURLToPath } from 'node:url'
import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

export default defineConfig(({ mode }) => ({
  plugins: [vue()],
  build: { target: 'es2022' },
  resolve: {
    // Vitest only. The Vue plugin turns `<img src="/favicon.svg">` into an import of
    // "/favicon.svg", a public-dir URL that the dev server and the build both understand. The
    // test runner resolves it as a file instead, and on Windows "file:///favicon.svg" is not an
    // absolute path, so every component that renders the logo failed to load there while the
    // same files passed on Linux. Pointing the import at the real file keeps the asset import in
    // place (the test still sees a URL string) without touching the dev or production resolution.
    alias: mode === 'test'
      ? [{ find: /^\/favicon\.svg$/, replacement: fileURLToPath(new URL('./public/favicon.svg', import.meta.url)) }]
      : []
  },
  server: {
    port: 5173,
    proxy: {
      '/api': { target: 'http://localhost:8080', changeOrigin: true },
      '/ws':  { target: 'ws://localhost:8080', ws: true }
    }
  }
}))
