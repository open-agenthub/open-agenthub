import { createApp } from 'vue'
import McpServersPane from '../../frontend/src/components/McpServersPane.vue'
import { api } from '../../frontend/src/api.js'
import '../../frontend/src/style.css'

const item = { id: 'demo', name: 'docs', kind: 'raw', mine: true, owner: 'demo',
  configJson: JSON.stringify({ type: 'http', url: 'https://mcp.example.com', headers: { Authorization: 'demo-value' } }) }
api.mcpServers = api.adminMcpServers = async () => [item]
api.libraryShares = async () => ({ all: true, users: [], groups: [] })
api.eeListGroups = async () => []
api.adminOverview = async () => ({ users: [] })
api.setLibraryShares = async () => { window.proofShared = true }
api.updateMcpServer = api.updateAdminMcpServer = async (id, body) => { window.proofSaved = body }
const org = new URLSearchParams(location.search).has('org')
createApp(McpServersPane, { mode: org ? 'org' : 'personal', isAdmin: true }).mount('#app')
