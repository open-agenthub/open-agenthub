<script setup>
import { computed, ref } from 'vue'
import { config } from '../api.js'

defineProps({ isAdmin: { type: Boolean, default: false } })
const copied = ref('')
const copyError = ref('')
// Shell quoting also covers an apostrophe in a configured path, without changing the issuer.
const command = computed(() => `claude mcp add --transport http agenthub '${config.mcpUrl.replaceAll("'", "'\\''")}'`)
const cursor = computed(() => JSON.stringify({ mcpServers: { agenthub: { url: config.mcpUrl } } }, null, 2))
async function copy(label, text) {
  try {
    await navigator.clipboard.writeText(text)
    copied.value = label
    copyError.value = ''
  } catch { copyError.value = 'Copy unavailable. Select and copy the text below.' }
}
</script>
<template>
  <section class="connection-pane">
    <h3>Connect to AgentHub</h3>
    <template v-if="config.mcpEnabled && config.mcpUrl">
      <p>Use AgentHub sessions from your MCP client over HTTP.</p>
      <p v-if="config.mcpAuthMode === 'oidc'">Your client opens a browser to sign in with your instance account and approve access.</p>
      <p v-else-if="config.mcpAuthMode === 'apiKey'">Your client opens a browser. Enter the instance API key and approve access.</p>
      <p v-else>This instance uses insecure development mode. The browser consent grants access as the local dev user.</p>
      <div class="card setup">
        <h4>Claude Code</h4>
        <button @click="copy('Claude Code', command)">Copy command</button>
        <pre data-mcp-command>{{ command }}</pre>
        <p>Then run <code>/mcp</code> in Claude Code and authenticate.</p>
      </div>
      <div class="card setup">
        <h4>Claude Desktop</h4>
        <p>In Settings → Connectors → Add custom connector, enter the name AgentHub and paste this remote MCP server URL. Connect and approve access in the browser.</p>
        <button @click="copy('Claude Desktop', config.mcpUrl)">Copy URL</button>
        <pre data-mcp-url>{{ config.mcpUrl }}</pre>
        <p>The connector service must be able to reach this URL.</p>
      </div>
      <div class="card setup">
        <h4>Cursor</h4>
        <p>Add this entry to <code>~/.cursor/mcp.json</code> (or your project's <code>.cursor/mcp.json</code>), then connect in MCP settings and sign in.</p>
        <button @click="copy('Cursor', cursor)">Copy config</button>
        <pre data-mcp-cursor>{{ cursor }}</pre>
      </div>
      <p v-if="copied" role="status">Copied {{ copied }}.</p>
      <p v-if="copyError" role="status">{{ copyError }}</p>
    </template>
    <p v-else-if="isAdmin" data-mcp-disabled>The built-in HTTP MCP endpoint is disabled. Set Helm <code>mcp.enabled: true</code> and configure its public HTTPS URL. For OIDC, allow <code>&lt;public_url&gt;/connect/oidc-callback</code> at your identity provider.</p>
  </section>
</template>
<style scoped>
.connection-pane { max-width: 760px; padding: 26px 30px; min-width: 0; }
h3 { font-size: 22px; margin: 0 0 16px; }
h4 { margin: 0 0 12px; }
p { color: var(--muted); font-size: 13px; line-height: 1.6; }
.setup { margin: 16px 0; padding: 18px; }
pre { white-space: pre-wrap; overflow-wrap: anywhere; font-size: 12px; user-select: text; }
button { font-size: 12px; }
</style>
