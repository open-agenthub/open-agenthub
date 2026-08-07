// Turns the ```mermaid placeholders emitted by markdown.js into inline SVG diagrams.
// The mermaid library (~1 MB) is loaded on demand the first time a diagram appears,
// so chats without diagrams never pay for it. Invalid or still-streaming diagram
// sources simply stay visible as code blocks — rendering is strictly best-effort.

let mermaidPromise = null
let renderCounter = 0

function loadMermaid() {
  mermaidPromise ??= import('mermaid').then(({ default: mermaid }) => {
    mermaid.initialize({
      startOnLoad: false,
      // Strict security: mermaid sanitizes text and blocks script/click handlers —
      // diagram sources are untrusted agent output.
      securityLevel: 'strict',
      theme: 'dark',
      fontFamily: 'inherit'
    })
    return mermaid
  })
  return mermaidPromise
}

/**
 * Renders every not-yet-processed mermaid placeholder under `root` (an element or
 * document fragment). Safe to call repeatedly — processed blocks are marked, and
 * blocks whose source does not parse (e.g. mid-stream drafts) are left as code and
 * retried on the next call with the grown source.
 */
export async function renderMermaidBlocks(root) {
  const blocks = root?.querySelectorAll?.('pre.md-mermaid')
  if (!blocks?.length) return
  let mermaid
  try { mermaid = await loadMermaid() } catch { return } // offline/build issue: keep code visible
  for (const block of blocks) {
    if (!block.isConnected) continue
    const source = block.textContent ?? ''
    let svg
    try {
      ;({ svg } = await mermaid.render(`agenthub-mmd-${++renderCounter}`, source))
    } catch {
      // The temp element mermaid inserts for parsing can leak on failure — drop it.
      document.getElementById(`dagenthub-mmd-${renderCounter}`)?.remove()
      continue
    }
    if (!block.isConnected) continue // re-rendered while we were busy
    const holder = document.createElement('div')
    holder.className = 'md-mermaid-svg'
    holder.innerHTML = svg
    block.replaceWith(holder)
  }
}
