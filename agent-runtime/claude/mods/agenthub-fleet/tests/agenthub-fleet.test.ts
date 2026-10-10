// Runs under `claude plugin test` (no session, login or network): the session agent's loopback
// endpoints are stubbed through the http.fetch event, the clock is the kit's.
import { expect, mock, test } from 'claude-code/testing'

const INBOX_URL = 'http://127.0.0.1:7681/agenthub/mod/inbox'
const HEARTBEAT_URL = 'http://127.0.0.1:7681/agenthub/mod/heartbeat'

type Reply = { messages?: unknown[]; status?: number; down?: boolean }

/** Stubs everything the mod touches and records what it asked Claude Code to do. */
function world(on, replies: Reply[]) {
  const clock = mock.clock(on)
  mock.env(on, { AGENTHUB_MOD_TOKEN: 'mod-secret' })
  const calls = {
    fetches: [] as { url: string; method: string; auth: string }[],
    submits: [] as string[],
    contexts: [] as (readonly string[] | undefined)[],
    aborts: [] as string[],
    statuses: [] as (string | undefined)[],
    logs: [] as string[]
  }
  on('session.start', () => ({ cwd: '/work' }))
  on('command.register', () => ({ value: undefined }))
  on('http.fetch', ($, e) => {
    const init = e.init ?? {}
    calls.fetches.push({ url: e.url, method: init.method ?? 'GET', auth: String(init.headers?.Authorization ?? '') })
    if (e.url === HEARTBEAT_URL) return { value: { status: 204, ok: true, headers: {}, text: '' } }
    const reply = replies.shift() ?? { messages: [] }
    if (reply.down) return { deny: 'connection refused' }
    const status = reply.status ?? 200
    return { value: { status, ok: status < 300, headers: {}, text: JSON.stringify({ messages: reply.messages ?? [] }) } }
  })
  on('prompt.submit', ($, e) => {
    calls.submits.push(e.text)
    calls.contexts.push(e.context)
    return { text: e.text }
  })
  on('turn.abort', ($, e) => {
    calls.aborts.push(e.turnId)
    return { value: undefined }
  })
  on('ui.status', ($, e) => {
    calls.statuses.push(e.text)
    return { value: undefined }
  })
  on('ui.log', ($, e) => {
    calls.logs.push(e.text)
    return { value: undefined }
  })
  on('turn.start', ($, e) => ({ turnId: e.turnId }))
  on('turn.complete', () => ({ text: '' }))
  return { clock, calls }
}

const priority = { id: 'm-1', from: 'rev-1', fromTitle: 'Code Reviewer', body: 'Please fix MR 42', priority: true, interrupt: false }
const interrupt = { ...priority, id: 'm-2', body: 'Stop, the branch is wrong', interrupt: true }
const plain = { id: 'm-3', from: 'rev-1', fromTitle: 'Code Reviewer', body: 'when you have a minute', priority: false, interrupt: false }

test('a priority message becomes the next prompt, headed with its sender', async ($, on) => {
  const { clock, calls } = world(on, [{ messages: [priority] }])

  await $.session.start({ surface: 'terminal', isInteractive: true, cwd: '/work' })
  await clock.advance(3000)

  expect(calls.submits).toEqual(['[AgentHub message from agent "Code Reviewer" (rev-1)]\nPlease fix MR 42'])
  expect(calls.aborts).toEqual([])
  // The heartbeat went out at start, the inbox poll with the mod's own token.
  expect(calls.fetches[0]).toEqual({ url: HEARTBEAT_URL, method: 'POST', auth: 'Bearer mod-secret' })
  expect(calls.fetches[1]).toEqual({ url: INBOX_URL, method: 'GET', auth: 'Bearer mod-secret' })
})

test('an interrupt aborts the running turn before the prompt is submitted', async ($, on) => {
  const { clock, calls } = world(on, [{ messages: [interrupt] }])

  await $.session.start({ surface: 'terminal', isInteractive: true, cwd: '/work' })
  await $.turn.start({ text: 'refactor everything', turnId: 'turn-7' })
  await clock.advance(3000)

  expect(calls.aborts).toEqual(['turn-7'])
  expect(calls.submits).toEqual(['[AgentHub message from agent "Code Reviewer" (rev-1)]\nStop, the branch is wrong'])
})

test('an interrupt after the turn completed submits without aborting anything', async ($, on) => {
  const { clock, calls } = world(on, [{ messages: [interrupt] }])

  await $.session.start({ surface: 'terminal', isInteractive: true, cwd: '/work' })
  await $.turn.start({ text: 'refactor everything', turnId: 'turn-7' })
  await $.turn.complete({ turnId: 'turn-7', answer: 'done', durationMs: 10, isAborted: false, reason: 'answer' })
  await clock.advance(3000)

  expect(calls.aborts).toEqual([])
  expect(calls.submits.length).toBe(1)
})

test('a plain message only sets the status line and is handed over by /inbox', async ($, on) => {
  const { clock, calls } = world(on, [{ messages: [plain] }])

  await $.session.start({ surface: 'terminal', isInteractive: true, cwd: '/work' })
  await clock.advance(3000)

  expect(calls.submits).toEqual([])
  expect(calls.statuses).toEqual(['📨 1 fleet message — /inbox'])

  const answer = await $.command.run({ command: 'inbox', args: '', origin: { kind: 'composer' } })
  expect(answer.text).toBe('[AgentHub message from agent "Code Reviewer" (rev-1)]\nwhen you have a minute')
  expect(calls.statuses[1]).toBeUndefined()

  const again = await $.command.run({ command: 'inbox', args: '', origin: { kind: 'composer' } })
  expect(again.text).toBe('No fleet messages are waiting.')
})

test('waiting messages ride along as context with the next prompt, once', async ($, on) => {
  const { clock, calls } = world(on, [{ messages: [plain] }])

  await $.session.start({ surface: 'terminal', isInteractive: true, cwd: '/work' })
  await clock.advance(3000)
  await $.prompt.submit({ text: 'carry on' })
  await $.prompt.submit({ text: 'and again' })

  expect(calls.submits).toEqual(['carry on', 'and again'])
  expect(calls.contexts[0]).toEqual(['[AgentHub message from agent "Code Reviewer" (rev-1)]\nwhen you have a minute'])
  expect(calls.contexts[1]).toBeUndefined()
})

test('a failed poll is a debug line and the timer keeps going', async ($, on) => {
  const { clock, calls } = world(on, [{ down: true }, { status: 500 }, { messages: [priority] }])

  await $.session.start({ surface: 'terminal', isInteractive: true, cwd: '/work' })
  await clock.advance(3000)
  await clock.advance(3000)
  expect(calls.submits).toEqual([])
  expect(calls.logs.length).toBe(2)
  expect(calls.logs[0]).toMatch(/fleet poll failed/)
  expect(calls.logs[1]).toMatch(/HTTP 500/)

  await clock.advance(3000)
  expect(calls.submits.length).toBe(1)
})

test('without a mod token the mod stays inert', async ($, on) => {
  const clock = mock.clock(on)
  mock.env(on, {})
  const fetches: string[] = []
  on('session.start', () => ({ cwd: '/work' }))
  on('command.register', () => ({ value: undefined }))
  on('http.fetch', ($, e) => {
    fetches.push(e.url)
    return { value: { status: 200, ok: true, headers: {}, text: '{"messages":[]}' } }
  })

  await $.session.start({ surface: 'terminal', isInteractive: true, cwd: '/work' })
  await clock.advance(9000)

  expect(fetches).toEqual([])
})
