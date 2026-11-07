// A stdio MCP server with the tools search, read and get-data.v2 (a dotted, hyphenated name), that appends every request it receives to
// $DOCS_MCP_LOG. A tool the policy blocks must never show up there as tools/call.
// Newline-delimited JSON-RPC, no SDK, so the proof does not depend on the session agent's modules.
import fs from 'node:fs'
import readline from 'node:readline'

const name = process.argv[2] || 'docs'
const logFile = process.env.DOCS_MCP_LOG
const log = line => { if (logFile) fs.appendFileSync(logFile, `${new Date().toISOString()} ${name} ${line}\n`) }
const send = message => process.stdout.write(JSON.stringify({ jsonrpc: '2.0', ...message }) + '\n')
const tools = ['search', 'read', 'get-data.v2'].map(tool => ({
  name: tool, description: `${tool} the ${name} documents`,
  inputSchema: { type: 'object', properties: { query: { type: 'string' } } }
}))

readline.createInterface({ input: process.stdin }).on('line', line => {
  let request
  try { request = JSON.parse(line) } catch { return }
  log(`${request.method}${request.method === 'tools/call' ? ' ' + request.params?.name : ''}`)
  if (request.id === undefined) return
  if (request.method === 'initialize')
    send({ id: request.id, result: { protocolVersion: request.params?.protocolVersion || '2025-06-18', capabilities: { tools: {} }, serverInfo: { name, version: '1.0.0' } } })
  else if (request.method === 'tools/list') send({ id: request.id, result: { tools } })
  else if (request.method === 'tools/call')
    send({ id: request.id, result: { content: [{ type: 'text', text: `EXECUTED ${name}/${request.params?.name}` }] } })
  else send({ id: request.id, error: { code: -32601, message: 'method not found' } })
})
