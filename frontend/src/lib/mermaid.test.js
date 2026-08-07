// @vitest-environment happy-dom
import { beforeEach, describe, expect, it, vi } from 'vitest'

const mocks = vi.hoisted(() => ({
  initialize: vi.fn(),
  render: vi.fn()
}))
vi.mock('mermaid', () => ({ default: { initialize: mocks.initialize, render: mocks.render } }))

import { renderMermaidBlocks } from './mermaid.js'

function mount(html) {
  const root = document.createElement('div')
  root.innerHTML = html
  document.body.appendChild(root)
  return root
}

describe('renderMermaidBlocks', () => {
  beforeEach(() => {
    document.body.innerHTML = ''
    mocks.render.mockReset()
  })

  it('replaces mermaid placeholders with the rendered SVG', async () => {
    mocks.render.mockResolvedValue({ svg: '<svg data-diagram></svg>' })
    const root = mount('<pre class="md-code md-mermaid"><code>graph TD; A--&gt;B</code></pre>')

    await renderMermaidBlocks(root)

    expect(mocks.render).toHaveBeenCalledTimes(1)
    expect(mocks.render.mock.calls[0][1]).toBe('graph TD; A-->B') // textContent un-escapes
    expect(root.querySelector('.md-mermaid-svg svg')).not.toBeNull()
    expect(root.querySelector('pre.md-mermaid')).toBeNull()
  })

  it('keeps the source code block visible when the diagram does not parse', async () => {
    mocks.render.mockRejectedValue(new Error('parse error'))
    const root = mount('<pre class="md-code md-mermaid"><code>graph TD; A--</code></pre>')

    await renderMermaidBlocks(root)

    expect(root.querySelector('pre.md-mermaid')).not.toBeNull()
    expect(root.querySelector('.md-mermaid-svg')).toBeNull()
  })

  it('initializes mermaid once with strict security', async () => {
    mocks.render.mockResolvedValue({ svg: '<svg></svg>' })
    const root = mount(
      '<pre class="md-mermaid"><code>a</code></pre><pre class="md-mermaid"><code>b</code></pre>')

    await renderMermaidBlocks(root)
    await renderMermaidBlocks(root)

    expect(mocks.initialize).toHaveBeenCalledTimes(1)
    expect(mocks.initialize.mock.calls[0][0]).toMatchObject({ startOnLoad: false, securityLevel: 'strict' })
  })

  it('is a no-op without placeholders or without a root', async () => {
    mocks.render.mockResolvedValue({ svg: '<svg></svg>' })
    await renderMermaidBlocks(mount('<p>no diagrams</p>'))
    await renderMermaidBlocks(null)
    expect(mocks.render).not.toHaveBeenCalled()
  })
})
