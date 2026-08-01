import { describe, expect, it } from 'vitest'
import { renderMarkdown } from './markdown.js'

describe('renderMarkdown', () => {
  it('escapes HTML before any transform runs', () => {
    const html = renderMarkdown('<script>alert(1)</script> & <img src=x onerror=y>')
    expect(html).not.toContain('<script>')
    expect(html).not.toContain('<img')
    expect(html).toContain('&lt;script&gt;')
    expect(html).toContain('&amp;')
  })

  it('renders emphasis, inline code, and safe links', () => {
    const html = renderMarkdown('Use **bold** and *italic* with `code()` — see [docs](https://example.com/a).')
    expect(html).toContain('<strong>bold</strong>')
    expect(html).toContain('<em>italic</em>')
    expect(html).toContain('<code>code()</code>')
    expect(html).toContain('<a href="https://example.com/a" target="_blank" rel="noopener noreferrer">docs</a>')
  })

  it('never links non-http protocols', () => {
    const html = renderMarkdown('[x](javascript:alert(1)) [y](data:text/html;base64,x)')
    expect(html).not.toContain('<a ')
  })

  it('keeps emphasis transforms out of inline code', () => {
    const html = renderMarkdown('run `git commit -m "**wip**"` now')
    expect(html).toContain('<code>git commit -m &quot;**wip**&quot;</code>')
    expect(html).not.toContain('<strong>wip</strong>')
  })

  it('renders fenced code blocks verbatim and escaped', () => {
    const html = renderMarkdown('before\n```js\nconst a = 1 < 2 // **not bold**\n```\nafter')
    expect(html).toContain('<pre class="md-code"><code>const a = 1 &lt; 2 // **not bold**</code></pre>')
    expect(html).toContain('<p>before</p>')
    expect(html).toContain('<p>after</p>')
  })

  it('renders headings and both list kinds', () => {
    const html = renderMarkdown('## Title\n- one\n- two\n\n1. first\n2. second')
    expect(html).toContain('<h4>Title</h4>')
    expect(html).toContain('<ul><li>one</li><li>two</li></ul>')
    expect(html).toContain('<ol><li>first</li><li>second</li></ol>')
  })

  it('returns empty output for empty input', () => {
    expect(renderMarkdown('')).toBe('')
    expect(renderMarkdown(null)).toBe('')
  })
})
