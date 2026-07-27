// Minimal escape-first markdown renderer for agent chat output. All input is
// HTML-escaped before any transform runs, so agent text can never inject
// markup — only the tags produced below can appear in the result.

function escapeHtml(text) {
  return text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;').replace(/'/g, '&#39;')
}

const SENTINEL = String.fromCharCode(0)

function renderInline(escaped) {
  const codeSpans = []
  // NUL sentinels cannot occur in escaped text, so code spans survive the
  // emphasis/link transforms untouched.
  let text = escaped.split(SENTINEL).join('').replace(/`([^`\n]+)`/g, (_, code) => {
    codeSpans.push(code)
    return SENTINEL + (codeSpans.length - 1) + SENTINEL
  })
  text = text
    .replace(/\[([^\]\n]+)\]\((https?:\/\/[^)\s]+)\)/g,
      '<a href="$2" target="_blank" rel="noopener noreferrer">$1</a>')
    .replace(/\*\*([^*\n]+)\*\*/g, '<strong>$1</strong>')
    .replace(/(^|[\s(])\*([^*\n]+)\*(?=[\s).,;:!?]|$)/g, '$1<em>$2</em>')
  return text.replace(new RegExp(SENTINEL + '(\\d+)' + SENTINEL, 'g'), (_, i) => `<code>${codeSpans[Number(i)]}</code>`)
}

function renderTextBlock(block) {
  const lines = block.split('\n')
  const html = []
  let list = null
  let paragraph = []

  const flushParagraph = () => {
    if (paragraph.length) html.push(`<p>${paragraph.map(renderInline).join('<br>')}</p>`)
    paragraph = []
  }
  const flushList = () => {
    if (list) html.push(`<${list.tag}>${list.items.map(item => `<li>${renderInline(item)}</li>`).join('')}</${list.tag}>`)
    list = null
  }

  for (const line of lines) {
    const heading = /^(#{1,4})\s+(.*)$/.exec(line)
    const bullet = /^\s*[-*]\s+(.*)$/.exec(line)
    const ordered = /^\s*\d+[.)]\s+(.*)$/.exec(line)
    if (heading) {
      flushParagraph(); flushList()
      const level = Math.min(heading[1].length + 2, 5)
      html.push(`<h${level}>${renderInline(heading[2])}</h${level}>`)
    } else if (bullet || ordered) {
      flushParagraph()
      const tag = bullet ? 'ul' : 'ol'
      if (!list || list.tag !== tag) { flushList(); list = { tag, items: [] } }
      list.items.push((bullet || ordered)[1])
    } else if (!line.trim()) {
      flushParagraph(); flushList()
    } else {
      flushList()
      paragraph.push(line)
    }
  }
  flushParagraph(); flushList()
  return html.join('')
}

export function renderMarkdown(source) {
  if (!source) return ''
  const escaped = escapeHtml(source.replace(/\r\n/g, '\n'))
  const parts = escaped.split(/^```[^\n]*$/m)
  return parts.map((part, index) => {
    if (index % 2 === 1) {
      const code = part.replace(/^\n/, '').replace(/\n$/, '')
      return `<pre class="md-code"><code>${code}</code></pre>`
    }
    return renderTextBlock(part)
  }).join('')
}
