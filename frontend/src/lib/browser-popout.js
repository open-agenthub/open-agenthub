// Keep the shared credential in memory: a URL would expose it to history and access logs.
export function openBrowserPopout(state, restored) {
  const child = window.open('/browser-popout', '_blank', 'popup,width=1280,height=900')
  if (!child) return null
  let finished = false
  const update = () => {
    if (!finished && !child.closed) child.postMessage({ type: 'browser-popout-state', ...state() }, location.origin)
  }
  const receive = event => {
    if (event.origin !== location.origin || event.source !== child) return
    if (event.data?.type === 'browser-popout-ready') update()
    if (event.data?.type === 'browser-popout-return') close()
  }
  const close = () => {
    if (finished) return
    finished = true
    clearInterval(timer)
    window.removeEventListener('message', receive)
    child.close()
    restored()
  }
  window.addEventListener('message', receive)
  // Polling also covers an OS window close and cross-origin navigation, where unload is unreliable.
  const timer = setInterval(() => { if (child.closed) close() }, 500)
  return { close, update }
}
