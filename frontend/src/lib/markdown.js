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
    .replace(/~~([^~\n]+)~~/g, '<del>$1</del>')
  return text.replace(new RegExp(SENTINEL + '(\\d+)' + SENTINEL, 'g'), (_, i) => `<code>${codeSpans[Number(i)]}</code>`)
}

// A GFM table separator row: pipes, dashes, optional alignment colons ("|---|:--:|").
const TABLE_SEPARATOR = /^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$/

/** Splits a pipe-table row into trimmed cells (boundary pipes dropped, \| kept). */
function tableCells(line) {
  const cells = line.replace(/\\\|/g, SENTINEL).split('|').map(c => c.split(SENTINEL).join('|').trim())
  if (cells.length && cells[0] === '') cells.shift()
  if (cells.length && cells[cells.length - 1] === '') cells.pop()
  return cells
}

function tableAlignments(separator) {
  return tableCells(separator).map(cell => {
    const left = cell.startsWith(':')
    const right = cell.endsWith(':')
    if (left && right) return ' style="text-align:center"'
    if (right) return ' style="text-align:right"'
    return ''
  })
}

function renderTableRow(cells, tag, aligns) {
  return '<tr>' + cells.map((cell, i) =>
    `<${tag}${aligns[i] ?? ''}>${renderInline(cell)}</${tag}>`).join('') + '</tr>'
}

function renderListItem(item) {
  // GFM task list: "[ ] text" / "[x] text" after the bullet.
  const task = /^\[([ xX])\]\s+(.*)$/.exec(item)
  if (!task) return `<li>${renderInline(item)}</li>`
  const checked = task[1] !== ' ' ? ' checked' : ''
  return `<li class="task"><input type="checkbox" disabled${checked}> ${renderInline(task[2])}</li>`
}

function renderTextBlock(block) {
  const lines = block.split('\n')
  const html = []
  let list = null
  let paragraph = []
  let quote = null

  const flushParagraph = () => {
    if (paragraph.length) html.push(`<p>${paragraph.map(renderInline).join('<br>')}</p>`)
    paragraph = []
  }
  const flushList = () => {
    if (list) html.push(`<${list.tag}>${list.items.map(renderListItem).join('')}</${list.tag}>`)
    list = null
  }
  const flushQuote = () => {
    if (quote) html.push(`<blockquote>${renderTextBlock(quote.join('\n'))}</blockquote>`)
    quote = null
  }

  for (let i = 0; i < lines.length; i++) {
    const line = lines[i]
    // The input is escaped, so a literal ">" quote marker arrives as "&gt;".
    const quoted = /^&gt;\s?(.*)$/.exec(line)
    if (quoted) {
      flushParagraph(); flushList()
      ;(quote ??= []).push(quoted[1])
      continue
    }
    flushQuote()

    const heading = /^(#{1,4})\s+(.*)$/.exec(line)
    const bullet = /^\s*[-*]\s+(.*)$/.exec(line)
    const ordered = /^\s*\d+[.)]\s+(.*)$/.exec(line)
    const rule = /^\s*(-{3,}|\*{3,}|_{3,})\s*$/.test(line)
    // A pipe table starts at a header row followed by a separator row.
    if (line.includes('|') && !bullet && !ordered && TABLE_SEPARATOR.test(lines[i + 1] ?? '')) {
      flushParagraph(); flushList()
      const aligns = tableAlignments(lines[i + 1])
      const rows = [`<thead>${renderTableRow(tableCells(line), 'th', aligns)}</thead>`]
      const body = []
      i += 1 // skip the separator row
      while (i + 1 < lines.length && lines[i + 1].includes('|') && lines[i + 1].trim()) {
        i += 1
        body.push(renderTableRow(tableCells(lines[i]), 'td', aligns))
      }
      if (body.length) rows.push(`<tbody>${body.join('')}</tbody>`)
      html.push(`<div class="md-table"><table>${rows.join('')}</table></div>`)
    } else if (heading) {
      flushParagraph(); flushList()
      const level = Math.min(heading[1].length + 2, 5)
      html.push(`<h${level}>${renderInline(heading[2])}</h${level}>`)
    } else if (rule) {
      flushParagraph(); flushList()
      html.push('<hr>')
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
  flushParagraph(); flushList(); flushQuote()
  return html.join('')
}

export function renderMarkdown(source) {
  if (!source) return ''
  const escaped = escapeHtml(source.replace(/\r\n/g, '\n'))
  // Split on fence lines, capturing the info string ("```mermaid"). With the capture
  // group the parts alternate: text, info, code, info, text, … — chunks sit at even
  // indices, and every second chunk is fenced code.
  const parts = escaped.split(/^```([^\n]*)$/m)
  const html = []
  for (let i = 0; i < parts.length; i += 2) {
    if ((i / 2) % 2 === 1) {
      const code = parts[i].replace(/^\n/, '').replace(/\n$/, '')
      const lang = (parts[i - 1] ?? '').trim().toLowerCase()
      // Mermaid fences become placeholders that lib/mermaid.js turns into inline
      // SVG after mount; until then (and on parse errors) the source stays visible.
      html.push(lang === 'mermaid'
        ? `<pre class="md-code md-mermaid"><code>${code}</code></pre>`
        : `<pre class="md-code"><code>${code}</code></pre>`)
    } else {
      html.push(renderTextBlock(parts[i]))
    }
  }
  return html.join('')
}
