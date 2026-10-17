// Runs under `claude plugin test` (no session, login or network): the session agent's loopback
// endpoints are stubbed through the http.fetch event, the clock is the kit's.
import { expect, mock, test } from 'claude-code/testing'

const INBOX_URL = 'http://127.0.0.1:7681/agenthub/mod/inbox'
const HEARTBEAT_URL = 'http://127.0.0.1:7681/agenthub/mod/heartbeat'
const LIMIT_URL = 'http://127.0.0.1:7681/agenthub/mod/limit'

type Reply = { messages?: unknown[]; status?: number; down?: boolean }

/** Stubs everything the mod touches and records what it asked Claude Code to do. */
function world(on, replies: Reply[], env: Record<string, string> = { AGENTHUB_MOD_TOKEN: 'mod-secret' }) {
  const clock = mock.clock(on)
  mock.env(on, env)
  const calls = {
    fetches: [] as { url: string; method: string; auth: string }[],
    limits: [] as { kind: string; percentUsed: number; resetsAt: string | null }[],
    submits: [] as string[],
    contexts: [] as (readonly string[] | undefined)[],
    aborts: [] as string[],
    statuses: [] as (string | undefined)[],
    logs: [] as string[],
    usage: { context: { window: 200000, tokens: 1000, percent: 1 }, rateLimits: [] as unknown[] },
    limitStatus: 202,
    refuseSubmit: false
  }
  on('session.start', () => ({ cwd: '/work' }))
  on('command.register', () => ({ value: undefined }))
  on('session.usage', () => ({ value: calls.usage }))
  // The engine answers session.measure with the units that moved; here the test is the engine.
  on('session.measure', ($, e) => ({ changed: e.changed }))
  on('http.fetch', ($, e) => {
    const init = e.init ?? {}
    calls.fetches.push({ url: e.url, method: init.method ?? 'GET', auth: String(init.headers?.Authorization ?? '') })
    if (e.url === HEARTBEAT_URL) return { value: { status: 204, ok: true, headers: {}, text: '' } }
    if (e.url === LIMIT_URL) {
      calls.limits.push(JSON.parse(String(init.body)))
      return { value: { status: calls.limitStatus, ok: calls.limitStatus < 300, headers: {}, text: '' } }
    }
    const reply = replies.shift() ?? { messages: [] }
    if (reply.down) return { deny: 'connection refused' }
    const status = reply.status ?? 200
    return { value: { status, ok: status < 300, headers: {}, text: JSON.stringify({ messages: reply.messages ?? [] }) } }
  })
  on('prompt.submit', ($, e) => {
    if (calls.refuseSubmit) return { deny: 'session closed' }
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

const plainText = '[AgentHub message from agent "Code Reviewer" (rev-1)]\nwhen you have a minute'

// #98: the message was held for a prompt the person might never type, after the session agent
// had told the hub it was delivered. An idle agent has to get it without anyone typing.
test('a plain message arriving while no turn runs becomes a prompt of its own', async ($, on) => {
  const { clock, calls } = world(on, [{ messages: [plain] }])

  await $.session.start({ surface: 'terminal', isInteractive: true, cwd: '/work' })
  await clock.advance(3000)

  expect(calls.submits).toEqual([plainText])
  expect(calls.aborts).toEqual([])
  expect(calls.statuses).toEqual([undefined])

  const answer = await $.command.run({ command: 'inbox', args: '', origin: { kind: 'composer' } })
  expect(answer.text).toBe('No fleet messages are waiting.')
})

test('a plain message during a turn waits on the status line and goes once the turn is over', async ($, on) => {
  const { clock, calls } = world(on, [{ messages: [plain] }])

  await $.session.start({ surface: 'terminal', isInteractive: true, cwd: '/work' })
  await $.turn.start({ text: 'refactor everything', turnId: 'turn-7' })
  await clock.advance(3000)
  expect(calls.submits).toEqual([])
  expect(calls.statuses).toEqual(['📨 1 fleet message — /inbox'])

  await $.turn.complete({ turnId: 'turn-7', answer: 'done', durationMs: 10, isAborted: false, reason: 'answer' })
  await clock.advance(3000)
  expect(calls.submits).toEqual([plainText])
  expect(calls.statuses[1]).toBeUndefined()

  // Released once: later idle polls find nothing to submit.
  await clock.advance(6000)
  expect(calls.submits.length).toBe(1)
})

test('during a turn /inbox hands the waiting messages over instead', async ($, on) => {
  const { clock, calls } = world(on, [{ messages: [plain] }])

  await $.session.start({ surface: 'terminal', isInteractive: true, cwd: '/work' })
  await $.turn.start({ text: 'refactor everything', turnId: 'turn-7' })
  await clock.advance(3000)

  const answer = await $.command.run({ command: 'inbox', args: '', origin: { kind: 'composer' } })
  expect(answer.text).toBe(plainText)
  expect(calls.statuses[1]).toBeUndefined()

  await $.turn.complete({ turnId: 'turn-7', answer: 'done', durationMs: 10, isAborted: false, reason: 'answer' })
  await clock.advance(3000)
  expect(calls.submits).toEqual([])
})

test('waiting messages ride along as context with the next prompt, once', async ($, on) => {
  const { clock, calls } = world(on, [{ messages: [plain] }])

  await $.session.start({ surface: 'terminal', isInteractive: true, cwd: '/work' })
  await $.turn.start({ text: 'refactor everything', turnId: 'turn-7' })
  await clock.advance(3000)
  await $.prompt.submit({ text: 'carry on' })
  await $.prompt.submit({ text: 'and again' })

  expect(calls.submits).toEqual(['carry on', 'and again'])
  expect(calls.contexts[0]).toEqual([plainText])
  expect(calls.contexts[1]).toBeUndefined()

  // Already handed over with the prompt: the end of the turn releases nothing more.
  await $.turn.complete({ turnId: 'turn-7', answer: 'done', durationMs: 10, isAborted: false, reason: 'answer' })
  await clock.advance(3000)
  expect(calls.submits.length).toBe(2)
})

test('a release the engine refuses keeps the message and tries again on the next idle poll', async ($, on) => {
  const { clock, calls } = world(on, [{ messages: [plain] }])
  calls.refuseSubmit = true

  await $.session.start({ surface: 'terminal', isInteractive: true, cwd: '/work' })
  await clock.advance(3000)
  expect(calls.submits).toEqual([])
  expect(calls.logs.some(line => /fleet prompt failed/.test(line))).toBe(true)
  expect(calls.statuses.at(-1)).toBe('📨 1 fleet message — /inbox')

  calls.refuseSubmit = false
  await clock.advance(3000)
  expect(calls.submits).toEqual([plainText])
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

// ---- usage limits (docs/account-limits.md) ---------------------------------------------------

const measure = (rateLimits: unknown[]) =>
  ({ context: { window: 200000, tokens: 1000, percent: 1 }, rateLimits, changed: ['rateLimits'] })
const fiveHourFull = { kind: 'five_hour', percentUsed: 100, resetsAt: '2026-10-11T15:00:00.000Z' }
const weekHalf = { kind: 'seven_day', percentUsed: 48.5, resetsAt: '2026-10-14T00:00:00.000Z' }

test('a window at 100 % is reported to the session agent once per reset, with the mod token', async ($, on) => {
  const { calls } = world(on, [])

  await $.session.start({ surface: 'terminal', isInteractive: true, cwd: '/work' })
  await $.session.measure(measure([fiveHourFull, weekHalf]))
  // The same reading again, as every turn raises it: nothing new to say.
  await $.session.measure(measure([fiveHourFull, weekHalf]))

  expect(calls.limits).toEqual([{ kind: 'five_hour', percentUsed: 100, resetsAt: '2026-10-11T15:00:00.000Z' }])
  const post = calls.fetches.find(f => f.url === LIMIT_URL)
  expect(post).toEqual({ url: LIMIT_URL, method: 'POST', auth: 'Bearer mod-secret' })

  // The window reset and filled up again: a new reset time is a new report.
  await $.session.measure(measure([{ ...fiveHourFull, resetsAt: '2026-10-11T20:00:00.000Z' }]))
  expect(calls.limits.length).toBe(2)
  expect(calls.limits[1].resetsAt).toBe('2026-10-11T20:00:00.000Z')
})

test('under the threshold nothing is reported, and the threshold comes from the environment', async ($, on) => {
  const { calls } = world(on, [], { AGENTHUB_MOD_TOKEN: 'mod-secret', AGENTHUB_LIMIT_THRESHOLD: '90' })

  await $.session.start({ surface: 'terminal', isInteractive: true, cwd: '/work' })
  await $.session.measure(measure([{ kind: 'five_hour', percentUsed: 89.9 }, weekHalf]))
  expect(calls.limits).toEqual([])

  await $.session.measure(measure([{ kind: 'five_hour', percentUsed: 90 }]))
  expect(calls.limits).toEqual([{ kind: 'five_hour', percentUsed: 90, resetsAt: null }])
})

test('a turn that ended on an API error reads the usage afresh and reports a full window', async ($, on) => {
  const { calls } = world(on, [])
  calls.usage = { context: { window: 200000, tokens: 1000, percent: 1 }, rateLimits: [fiveHourFull] }

  await $.session.start({ surface: 'terminal', isInteractive: true, cwd: '/work' })
  await $.turn.start({ text: 'go', turnId: 'turn-1' })
  await $.turn.complete({ turnId: 'turn-1', answer: '', durationMs: 10, isAborted: false, reason: 'error' })

  expect(calls.limits).toEqual([{ kind: 'five_hour', percentUsed: 100, resetsAt: '2026-10-11T15:00:00.000Z' }])
  // A later measurement with the same window does not repeat it.
  await $.session.measure(measure([fiveHourFull]))
  expect(calls.limits.length).toBe(1)
})

test('a refused report is retried on the next measurement, a failed one is a debug line', async ($, on) => {
  const { calls } = world(on, [])
  calls.limitStatus = 503

  await $.session.start({ surface: 'terminal', isInteractive: true, cwd: '/work' })
  await $.session.measure(measure([fiveHourFull]))
  expect(calls.limits.length).toBe(1)
  expect(calls.logs.some(line => /HTTP 503/.test(line))).toBe(true)

  calls.limitStatus = 202
  await $.session.measure(measure([fiveHourFull]))
  expect(calls.limits.length).toBe(2)
  await $.session.measure(measure([fiveHourFull]))
  expect(calls.limits.length).toBe(2)
})

test('without a mod token the mod stays inert', async ($, on) => {
  const clock = mock.clock(on)
  mock.env(on, {})
  const fetches: string[] = []
  on('session.start', () => ({ cwd: '/work' }))
  on('command.register', () => ({ value: undefined }))
  on('session.measure', ($, e) => ({ changed: e.changed }))
  on('http.fetch', ($, e) => {
    fetches.push(e.url)
    return { value: { status: 200, ok: true, headers: {}, text: '{"messages":[]}' } }
  })

  await $.session.start({ surface: 'terminal', isInteractive: true, cwd: '/work' })
  await clock.advance(9000)
  await $.session.measure({ context: { window: 200000, percent: 1 }, rateLimits: [{ kind: 'five_hour', percentUsed: 100 }], changed: ['rateLimits'] })

  expect(fetches).toEqual([])
})
