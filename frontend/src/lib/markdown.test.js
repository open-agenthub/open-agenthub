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

  it('renders GFM pipe tables with alignment and inline markup in cells', () => {
    const html = renderMarkdown(
      '| Name | Count | Note |\n| --- | ---: | :---: |\n| **a** | 1 | x |\n| b | 22 | `y` |')
    expect(html).toContain('<div class="md-table"><table>')
    expect(html).toContain('<thead><tr><th>Name</th><th style="text-align:right">Count</th><th style="text-align:center">Note</th></tr></thead>')
    expect(html).toContain('<td><strong>a</strong></td>')
    expect(html).toContain('<td style="text-align:right">22</td>')
    expect(html).toContain('<td style="text-align:center"><code>y</code></td>')
  })

  it('keeps escaped pipes inside table cells', () => {
    const html = renderMarkdown('| a | b |\n| --- | --- |\n| x \\| y | z |')
    expect(html).toContain('<td>x | y</td>')
  })

  it('does not treat a lone pipe line without separator as a table', () => {
    const html = renderMarkdown('a | b\nplain text')
    expect(html).not.toContain('<table>')
    expect(html).toContain('a | b')
  })

  it('renders blockquotes, horizontal rules, and strikethrough', () => {
    const html = renderMarkdown('> quoted **line**\n> second\n\n---\n\n~~gone~~ kept')
    expect(html).toContain('<blockquote><p>quoted <strong>line</strong><br>second</p></blockquote>')
    expect(html).toContain('<hr>')
    expect(html).toContain('<del>gone</del> kept')
  })

  it('renders task lists with disabled checkboxes', () => {
    const html = renderMarkdown('- [x] done\n- [ ] open')
    expect(html).toContain('<li class="task"><input type="checkbox" disabled checked> done</li>')
    expect(html).toContain('<li class="task"><input type="checkbox" disabled> open</li>')
  })

  it('marks mermaid fences for client-side rendering, escaped', () => {
    const html = renderMarkdown('```mermaid\ngraph TD; A-->B & C<D\n```\n```js\nconst x = 1\n```')
    expect(html).toContain('<pre class="md-code md-mermaid"><code>graph TD; A--&gt;B &amp; C&lt;D</code></pre>')
    expect(html).toContain('<pre class="md-code"><code>const x = 1</code></pre>')
    expect(html).not.toContain('md-mermaid"><code>const x')
  })
})
