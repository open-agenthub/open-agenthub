// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { openSessionEvents } from './live.js'

// happy-dom ships a WebSocket that would actually try to connect. A controllable stand-in keeps
// the reconnect behaviour observable without a server and without real timing.
class FakeSocket {
  static opened = []

  constructor(url) {
    this.url = url
    this.onopen = null
    this.onmessage = null
    this.onclose = null
    this.onerror = null
    this.closed = false
    FakeSocket.opened.push(this)
  }

  close() {
    this.closed = true
    this.onclose?.()
  }

  // Test-side triggers, named apart from the handlers they fire.
  accept() { this.onopen?.() }
  deliver(payload) { this.onmessage?.({ data: payload }) }
  drop() { this.onclose?.() }
}

describe('openSessionEvents', () => {
  beforeEach(() => {
    vi.useFakeTimers()
    FakeSocket.opened = []
    globalThis.WebSocket = FakeSocket
  })
  afterEach(() => vi.useRealTimers())

  it('reports event kinds and ignores the keep-alive', async () => {
    const events = []
    const stream = openSessionEvents(async () => 'wss://host/ws/sessions/s1/events', kind => events.push(kind))
    await vi.runOnlyPendingTimersAsync()

    const socket = FakeSocket.opened[0]
    expect(socket.url).toBe('wss://host/ws/sessions/s1/events')
    socket.accept()
    socket.deliver(JSON.stringify({ type: 'files' }))
    socket.deliver(JSON.stringify({ type: 'ping' }))
    socket.deliver('not json')
    socket.deliver(JSON.stringify({ type: 'files' }))

    expect(events).toEqual(['files', 'files'])
    stream.close()
  })

  it('reconnects with a growing delay and resets it once a connection is accepted', async () => {
    const stream = openSessionEvents(async () => 'wss://host/events', () => {})
    await vi.runOnlyPendingTimersAsync()
    expect(FakeSocket.opened).toHaveLength(1)

    FakeSocket.opened[0].drop()
    // 750ms is the first backoff step; stopping just short of it must not reconnect yet, or the
    // backoff is not actually being applied.
    await vi.advanceTimersByTimeAsync(700)
    expect(FakeSocket.opened).toHaveLength(1)
    await vi.advanceTimersByTimeAsync(100)
    expect(FakeSocket.opened).toHaveLength(2)

    FakeSocket.opened[1].drop()
    await vi.advanceTimersByTimeAsync(800)
    expect(FakeSocket.opened).toHaveLength(2) // second step is 1500ms, not 750ms again
    await vi.advanceTimersByTimeAsync(800)
    expect(FakeSocket.opened).toHaveLength(3)

    // A backend that comes back must not leave the client on a minutes-long delay, so an
    // accepted connection has to return the sequence to its first step.
    FakeSocket.opened[2].accept()
    FakeSocket.opened[2].drop()
    await vi.advanceTimersByTimeAsync(800)
    expect(FakeSocket.opened).toHaveLength(4)

    stream.close()
  })

  it('reports the connection state so a caller can slow its fallback poll', async () => {
    const states = []
    const stream = openSessionEvents(async () => 'wss://host/events', () => {}, state => states.push(state))
    await vi.runOnlyPendingTimersAsync()

    FakeSocket.opened[0].accept()
    FakeSocket.opened[0].drop()

    expect(states).toEqual([true, false])
    stream.close()
  })

  it('stops reconnecting once closed', async () => {
    const stream = openSessionEvents(async () => 'wss://host/events', () => {})
    await vi.runOnlyPendingTimersAsync()

    stream.close()
    await vi.advanceTimersByTimeAsync(60_000)

    expect(FakeSocket.opened).toHaveLength(1)
    expect(FakeSocket.opened[0].closed).toBe(true)
  })

  /// A pane torn down while the token request is in flight must not leave a socket behind.
  it('does not open a socket when closed while resolving the url', async () => {
    let release
    const stream = openSessionEvents(() => new Promise(resolve => { release = resolve }), () => {})

    stream.close()
    release('wss://host/events')
    await vi.runOnlyPendingTimersAsync()

    expect(FakeSocket.opened).toHaveLength(0)
  })

  it('retries when the url cannot be resolved', async () => {
    let attempts = 0
    const stream = openSessionEvents(async () => {
      if (++attempts === 1) throw new Error('token expired')
      return 'wss://host/events'
    }, () => {})
    // Zero, not runOnlyPendingTimers: the rejected attempt has to settle without the retry
    // timer it scheduled firing in the same step, or there is nothing left to observe.
    await vi.advanceTimersByTimeAsync(0)

    expect(FakeSocket.opened).toHaveLength(0)
    await vi.advanceTimersByTimeAsync(800)

    expect(attempts).toBe(2)
    expect(FakeSocket.opened).toHaveLength(1)
    stream.close()
  })
})
