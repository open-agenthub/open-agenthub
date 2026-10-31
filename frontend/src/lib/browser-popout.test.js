// @vitest-environment happy-dom
import { afterEach, expect, it, vi } from 'vitest'
import { openBrowserPopout } from './browser-popout.js'
afterEach(() => { vi.restoreAllMocks(); vi.useRealTimers() })
it('hands state only to its own same-origin child and restores once on window close', () => {
  vi.useFakeTimers()
  const child = { closed: false, postMessage: vi.fn(), close: vi.fn() }
  const open = vi.spyOn(window, 'open').mockReturnValue(child)
  const restored = vi.fn()
  const state = { session: { id: 's1' }, sharedToken: 'private-share' }
  const controller = openBrowserPopout(() => state, restored)
  expect(open.mock.calls[0][0]).toBe('/browser-popout')
  const message = (source, origin) => window.dispatchEvent(new MessageEvent('message', {
    source, origin, data: { type: 'browser-popout-ready' }
  }))
  message(child, 'https://example.com')
  message(window, location.origin)
  expect(child.postMessage).not.toHaveBeenCalled()
  message(child, location.origin)
  expect(child.postMessage).toHaveBeenCalledWith({ type: 'browser-popout-state', ...state }, location.origin)
  child.closed = true
  vi.advanceTimersByTime(500)
  expect(restored).toHaveBeenCalledTimes(1)
  controller.close()
  expect(restored).toHaveBeenCalledTimes(1)
})
it('handles a blocked pop-up without changing the embedded view', () => {
  vi.spyOn(window, 'open').mockReturnValue(null)
  const restored = vi.fn()
  expect(openBrowserPopout(() => ({}), restored)).toBeNull()
  expect(restored).not.toHaveBeenCalled()
})

it('uses drop coordinates as window features while leaving the URL credential-free', () => {
  const child = { closed: false, postMessage: vi.fn(), close: vi.fn() }
  const open = vi.spyOn(window, 'open').mockReturnValue(child)
  const controller = openBrowserPopout(() => ({ sharedToken: 'private-share' }), vi.fn(),
    { screenX: 401.4, screenY: 202.8 })
  expect(open).toHaveBeenCalledWith('/browser-popout', '_blank', 'popup,width=1280,height=900,left=401,top=203')
  controller.close()
})
