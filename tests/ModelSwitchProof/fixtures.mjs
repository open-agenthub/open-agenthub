// What both proofs share: a mock model that tells, in every answer, which model it was asked for
// and whether the conversation's first turn is still in the request, and the HOME each pinned CLI
// needs to talk to it without a login.
import http from 'node:http'
import fs from 'node:fs'
import path from 'node:path'
import crypto from 'node:crypto'

export const MARKER = 'PAPAYA42'
// A prompt carrying this word is answered only after `slowMs`: a turn that is still running.
export const SLOW = 'SLOWTURN'

export function replyFor(entry) {
  return `MODEL=${entry.model} CONTEXT=${entry.markerInRequest ? 'KEPT' : 'NONE'}`
}

/**
 * Starts the mock on a free port. `requests()` lists one entry per model request, never a header.
 * Kept in memory and printed by the proofs, not written to a file: what a request carried goes
 * nowhere but the run's own output.
 */
export async function startMock({ slowMs = 10_000 } = {}) {
  const entries = []
  const server = http.createServer((req, res) => {
    let body = ''
    req.on('data', chunk => { body += chunk })
    req.on('end', () => {
      let json = {}
      try { json = JSON.parse(body) } catch {}
      if (req.url.includes('/models')) {
        res.writeHead(200, { 'content-type': 'application/json' })
        return res.end(JSON.stringify({ object: 'list', data: [{ id: 'mock-a', object: 'model' }, { id: 'mock-b', object: 'model' }], models: [] }))
      }
      const text = JSON.stringify(json.messages || json.input || '')
      // Only the last user turn decides whether this one is slow; the history repeats earlier ones.
      const lastUser = JSON.stringify((json.messages || json.input || []).filter?.(m => m.role === 'user').at?.(-1) || '')
      // The last user message as the model got it: a switch must not leave anything in the prompt
      // that the next turn would carry (a typed command, a picker search).
      const lastUserMessage = (json.messages || json.input || []).filter?.(m => m.role === 'user').at?.(-1)
      const content = lastUserMessage?.content
      const lastUserText = (typeof content === 'string' ? content
        : Array.isArray(content) ? content.map(part => part?.text || '').join('\n') : '').slice(-300)
      const entry = { at: new Date().toISOString(), url: req.url.split('?')[0], model: json.model, stream: !!json.stream,
        markerInRequest: text.includes(MARKER), lastUserText }
      entries.push(entry)
      const delay = lastUser.includes(SLOW) ? slowMs : 0
      setTimeout(() => answer(req, res, json, entry), delay)
    })
  })
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve))
  return { port: server.address().port, close: () => server.close(), requests: () => entries.slice() }
}

function answer(req, res, json, entry) {
  if (req.url.startsWith('/v1/messages')) {
    if (req.url.includes('count_tokens')) { res.writeHead(200, { 'content-type': 'application/json' }); return res.end('{"input_tokens":1}') }
    if (!json.stream) {
      res.writeHead(200, { 'content-type': 'application/json' })
      return res.end(JSON.stringify({ id: 'm', type: 'message', role: 'assistant', model: json.model, content: [{ type: 'text', text: replyFor(entry) }], stop_reason: 'end_turn', usage: { input_tokens: 1, output_tokens: 1 } }))
    }
    res.writeHead(200, { 'content-type': 'text/event-stream' })
    const ev = (type, data) => res.write('event: ' + type + '\ndata: ' + JSON.stringify({ type, ...data }) + '\n\n')
    ev('message_start', { message: { id: 'msg_' + crypto.randomUUID(), type: 'message', role: 'assistant', model: json.model, content: [], stop_reason: null, usage: { input_tokens: 1, output_tokens: 1 } } })
    ev('content_block_start', { index: 0, content_block: { type: 'text', text: '' } })
    ev('content_block_delta', { index: 0, delta: { type: 'text_delta', text: replyFor(entry) } })
    ev('content_block_stop', { index: 0 })
    ev('message_delta', { delta: { stop_reason: 'end_turn' }, usage: { output_tokens: 1 } })
    ev('message_stop', {})
    return res.end()
  }
  if (req.url.includes('/chat/completions')) {
    res.writeHead(200, { 'content-type': 'text/event-stream' })
    const chunk = d => res.write('data: ' + JSON.stringify({ id: 'c', object: 'chat.completion.chunk', created: 1, model: json.model, choices: [d] }) + '\n\n')
    chunk({ index: 0, delta: { role: 'assistant', content: replyFor(entry) } })
    chunk({ index: 0, delta: {}, finish_reason: 'stop' })
    res.write('data: [DONE]\n\n')
    return res.end()
  }
  if (req.url.includes('/responses')) {
    res.writeHead(200, { 'content-type': 'text/event-stream' })
    const ev = (type, data) => res.write('event: ' + type + '\ndata: ' + JSON.stringify({ type, ...data }) + '\n\n')
    const item = { type: 'message', id: 'msg_1', role: 'assistant', status: 'completed', content: [{ type: 'output_text', text: replyFor(entry), annotations: [] }] }
    ev('response.created', { response: { id: 'resp_1' } })
    ev('response.output_item.done', { output_index: 0, item })
    ev('response.completed', { response: { id: 'resp_1', usage: { input_tokens: 1, input_tokens_details: null, output_tokens: 1, output_tokens_details: null, total_tokens: 2 } } })
    return res.end()
  }
  res.writeHead(404)
  res.end()
}

/**
 * Writes what `agent` needs under `home` to reach the mock on `port`, and returns the CLI
 * environment and its own start command (the PTY rig uses the command; the session agent builds
 * its own through the driver).
 */
export function prepareAgent(agent, { home, work, port, clis, startModel, customEndpoint, sessionId }) {
  const mkdir = p => fs.mkdirSync(p, { recursive: true })
  const env = {}
  if (agent === 'claude') {
    const key = 'sk-ant-api03-' + 'x'.repeat(80)
    fs.writeFileSync(path.join(home, '.claude.json'), JSON.stringify({
      hasCompletedOnboarding: true, theme: 'dark',
      customApiKeyResponses: { approved: [key.slice(-20)], rejected: [] },
      projects: { [work]: { hasTrustDialogAccepted: true, hasCompletedProjectOnboarding: true } }
    }))
    Object.assign(env, { ANTHROPIC_API_KEY: key, ANTHROPIC_BASE_URL: 'http://127.0.0.1:' + port })
    if (startModel) env.ANTHROPIC_MODEL = startModel
    return { env, cmd: process.env.CLAUDE_BIN || '/usr/local/bin/claude', args: ['--session-id', sessionId || crypto.randomUUID()] }
  }
  if (agent === 'codex') {
    mkdir(path.join(home, '.codex'))
    fs.writeFileSync(path.join(home, '.codex/auth.json'), JSON.stringify({ OPENAI_API_KEY: 'sk-test' }))
    // customEndpoint writes the provider block the runtime writes for a session's own endpoint
    // (codex/model-provider.js), so the switch is tried against what such a session runs.
    const model = customEndpoint
      ? `model = "${startModel || 'mock-a'}"\nmodel_provider = "agenthub_endpoint"\n`
      : (startModel ? `model = "${startModel}"\n` : '')
    const provider = customEndpoint
      ? `[model_providers.agenthub_endpoint]\nname = "Custom endpoint"\nbase_url = "http://127.0.0.1:${port}/v1"\nwire_api = "responses"\n`
      : ''
    fs.writeFileSync(path.join(home, '.codex/config.toml'),
      model + `check_for_update_on_startup = false\ncli_auth_credentials_store = "file"\nopenai_base_url = "http://127.0.0.1:${port}/v1"\n${provider}[projects."${work}"]\ntrust_level = "trusted"\n`)
    env.OPENAI_API_KEY = 'sk-test'
    return { env, cmd: path.join(clis, 'codex'), args: ['--no-alt-screen', '--no-daemon'] }
  }
  if (agent === 'opencode') {
    mkdir(path.join(home, '.config/opencode'))
    fs.writeFileSync(path.join(home, '.config/opencode/opencode.json'), JSON.stringify({
      $schema: 'https://opencode.ai/config.json', autoupdate: false, share: 'disabled',
      model: startModel || 'mock/mock-a',
      provider: { mock: { npm: '@ai-sdk/openai-compatible', name: 'Mock', options: { baseURL: 'http://127.0.0.1:' + port + '/v1', apiKey: 'x' }, models: { 'mock-a': { name: 'Mock A' }, 'mock-b': { name: 'Mock B' } } } }
    }))
    return { env, cmd: path.join(clis, 'opencode'), args: [] }
  }
  if (agent === 'openclaw') {
    // OpenClaw takes no endpoint from the environment; a config provider points it at the mock.
    const state = path.join(home, '.openclaw')
    mkdir(state)
    fs.writeFileSync(path.join(state, 'openclaw.json'), JSON.stringify({
      models: { providers: { mock: { baseUrl: 'http://127.0.0.1:' + port + '/v1', apiKey: 'mock-key', api: 'openai-completions', models: [{ id: 'mock-a', name: 'mock-a' }, { id: 'mock-b', name: 'mock-b' }] } } },
      agents: { defaults: { model: { primary: startModel || 'mock/mock-a' }, workspace: work } }
    }, null, 2))
    Object.assign(env, { OPENCLAW_STATE_DIR: state, OPENCLAW_CONFIG_PATH: path.join(state, 'openclaw.json') })
    return { env, cmd: path.join(clis, 'openclaw'), args: ['tui', '--local'] }
  }
  throw new Error('unknown agent ' + agent)
}
