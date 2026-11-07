// @vitest-environment happy-dom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { HANDSHAKE_TIMEOUT_MS, limitHandshake } from './handshake.js'

// The browser's behaviour, reduced: close() on a CONNECTING socket fails it and fires close.
class Socket extends EventTarget {
  readyState = 0
  open() { this.readyState = 1; this.dispatchEvent(new Event('open')) }
  close() { if (this.readyState < 2) { this.readyState = 3; this.dispatchEvent(new Event('close')) } }
}

describe('limitHandshake (#520)', () => {
  afterEach(() => vi.useRealTimers())

  it('fails a handshake that is still pending after the timeout, so the caller retries', () => {
    vi.useFakeTimers()
    const socket = new Socket()
    const closed = vi.fn()
    socket.addEventListener('close', closed)
    limitHandshake(socket)
    vi.advanceTimersByTime(HANDSHAKE_TIMEOUT_MS - 1)
    expect(closed).not.toHaveBeenCalled()
    vi.advanceTimersByTime(1)
    expect(closed).toHaveBeenCalledTimes(1)
  })

  it('leaves an opened socket alone', () => {
    vi.useFakeTimers()
    const socket = new Socket()
    const closed = vi.fn()
    socket.addEventListener('close', closed)
    limitHandshake(socket)
    socket.open()
    vi.advanceTimersByTime(HANDSHAKE_TIMEOUT_MS * 2)
    expect(closed).not.toHaveBeenCalled()
    expect(socket.readyState).toBe(1)
  })
})
