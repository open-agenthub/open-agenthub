// Runs the real HTTP/token/MCP paths against the backend and a rootless API server.
import assert from 'node:assert/strict'
import fs from 'node:fs'
import { execFileSync } from 'node:child_process'
import { createRequire } from 'node:module'
import { fileURLToPath } from 'node:url'
const repo = fileURLToPath(new URL('../../', import.meta.url))
const require = createRequire(repo + 'mcp/agenthub/package.json')
const { Client } = require('@modelcontextprotocol/sdk/client/index.js')
const { StdioClientTransport } = require('@modelcontextprotocol/sdk/client/stdio.js')
const base = process.env.PROOF_URL || 'http://127.0.0.1:8080'
const baseline = process.argv.includes('--baseline')
const kubectl = (...args) => JSON.parse(execFileSync(process.env.KUBECTL || 'kubectl', ['--kubeconfig', process.env.PROOF_KUBECONFIG, '-n', 'agenthub-sessions', ...args, '-o', 'json'], {encoding:'utf8'}))
async function api(method, path, body, token, status = 200) {
  const response = await fetch(base + path, {method, headers:{'Content-Type':'application/json', ...(token ? {Authorization:`Bearer ${token}`} : {})}, body: body === undefined ? undefined : JSON.stringify(body)})
  const raw = await response.text()
  assert.equal(response.status, status, `${method} ${path}: ${raw}`)
  try { return JSON.parse(raw) } catch { return raw }
}
const create = {title:'Schedule proof', mode:'Scheduled', agent:'Claude', authMode:'ApiKey', prompt:'Run the scheduled proof', schedule:'0 6 * * *'}
const session = await api('POST','/api/sessions',create)
const name = `session-${session.id}`
const cron = () => kubectl('get','cronjob',name)
const original = cron()
const job = kubectl('create','job',`proof-${session.id}`,'--from',`cronjob/${name}`)
kubectl('patch','job',job.metadata.name,'--subresource=status','--type=merge','-p',JSON.stringify({status:{active:1,startTime:new Date().toISOString()}}))
kubectl('patch','cronjob',name,'--subresource=status','--type=merge','-p',JSON.stringify({status:{active:[{apiVersion:'batch/v1',kind:'Job',name:job.metadata.name,namespace:'agenthub-sessions',uid:job.metadata.uid}]}}))
const activeBefore = kubectl('get','job',job.metadata.name)
const token = await api('POST','/api/tokens',{name:'schedule-proof'})
const updated = await api('PATCH',`/api/remote/sessions/${session.id}`,{schedule:'0 9 * * 1-5'},token.token)
if (baseline) {
  assert.equal(updated.schedule, create.schedule)
  assert.equal(cron().spec.schedule, create.schedule)
  console.log('PASS baseline reproduces token schedule update silently ignored')
  process.exit(0)
}
assert.equal(updated.schedule,'0 9 * * 1-5')
assert.ok(Date.parse(updated.nextRunAt) > Date.now())
assert.equal(cron().metadata.uid, original.metadata.uid)
assert.equal(cron().spec.schedule,updated.schedule)
assert.deepEqual(cron().spec.jobTemplate,original.spec.jobTemplate)
assert.deepEqual(kubectl('get','job',job.metadata.name),activeBefore)
console.log('PASS token update changes existing CronJob; active Job, template and history preserved')
for (const invalid of ['', '60 * * * *', 'bad', '0 0 0 * * *']) {
  await api('PATCH',`/api/remote/sessions/${session.id}`,{title:'must not be saved',schedule:invalid},token.token,400)
  await api('POST','/api/sessions',{...create,schedule:invalid},undefined,400)
}
assert.equal((await api('GET',`/api/sessions/${session.id}`)).title,create.title)
assert.equal(cron().spec.schedule,updated.schedule)
await api('PATCH',`/api/sessions/${session.id}`,{schedule:'0 10 * * *',systemPrompt:'forbidden'},undefined,400)
await api('PATCH',`/api/remote/sessions/${session.id}`,{title:'Renamed'},token.token)
assert.equal(cron().spec.schedule,updated.schedule)
const interactive=await api('POST','/api/sessions',{title:'Interactive proof',agent:'Claude',authMode:'ApiKey'})
await api('PATCH',`/api/remote/sessions/${interactive.id}`,{schedule:'0 10 * * *'},token.token,400)
await api('PATCH',`/api/remote/sessions/${session.id}`,{schedule:'0 10 * * *'},'invalid',401)
console.log('PASS create/update validation, mode guard, runtime guard, omitted schedule and token authentication')
const transport = new StdioClientTransport({command:process.execPath,args:[repo+'mcp/agenthub/server.mjs'],env:{...process.env,AGENTHUB_URL:base,AGENTHUB_TOKEN:token.token},stderr:'pipe'})
const mcp = new Client({name:'schedule-proof',version:'1.0.0'})
try {
  await mcp.connect(transport)
  assert.ok((await mcp.listTools()).tools.some(t=>t.name==='session_update' && t.inputSchema.properties.schedule))
  const reply=await mcp.callTool({name:'session_update',arguments:{sessionId:session.id,schedule:'0 11 * * *'}})
  assert.ok(!reply.isError,JSON.stringify(reply))
  const info=JSON.parse(reply.content[0].text)
  assert.equal(info.schedule,'0 11 * * *'); assert.ok(info.nextRunAt)
  assert.equal(cron().spec.schedule,info.schedule)
  assert.deepEqual(kubectl('get','job',job.metadata.name),activeBefore)
  console.log('PASS real stdio MCP edits the schedule and returns nextRunAt; active Job unchanged')
} finally {await mcp.close()}
if (process.env.PROOF_OUTPUT) fs.writeFileSync(process.env.PROOF_OUTPUT,JSON.stringify({sessionId:session.id,job:job.metadata.name,activeBefore,cronUid:original.metadata.uid},null,2))
