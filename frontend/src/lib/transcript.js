const SPINNER_GLYPHS = new Set(['*', '·', '…', '✢', '✣', '✳', '✶', '✻', '✽'])
const ELAPSED_TOKENS = /^(?:[*·…✢✣✳✶✻✽]\s*)*\d+s\s*·\s*↓\s*[\d.]+\s*[kKmM]?\s*tokens\)?$/u
const MAX_SHORT_CODE_POINTS = 12
const MIN_SHORT_BURST = 4
const MAX_PACKED_CODE_POINTS = 1200
const MAX_PACKED_BLOCKS = 20

const codePointLength = value => [...value].length

function baseTranscriptBlocks(text) {
  const lines = String(text ?? '').replace(/\r\n?/g, '\n').split('\n')
  const blocks = []
  let current = []
  let blankRun = 0

  const finishBlock = () => {
    if (current.length) blocks.push(current.join('\n'))
    current = []
  }

  for (const line of lines) {
    if (line.trim() === '') {
      blankRun += 1
      continue
    }

    if (blankRun >= 2) finishBlock()
    current.push(line)
    blankRun = 0
  }

  finishBlock()
  return blocks
}

function isKnownTransient(block) {
  const value = block.trim()
  if (!value || value.includes('\n')) return false
  const spinnerOnly = [...value].every(char => SPINNER_GLYPHS.has(char) || /\s/u.test(char))
  return spinnerOnly || ELAPSED_TOKENS.test(value)
}

function isShortBlock(block) {
  const value = block.trim()
  const nonEmptyLines = value.split('\n').filter(line => line.trim() !== '').length
  return codePointLength(value) <= MAX_SHORT_CODE_POINTS && nonEmptyLines <= 2
}

function suppressShortBursts(blocks) {
  const retained = []
  let shortRun = []

  const flush = () => {
    if (shortRun.length < MIN_SHORT_BURST) retained.push(...shortRun)
    shortRun = []
  }

  for (const block of blocks) {
    if (isShortBlock(block)) shortRun.push(block)
    else {
      flush()
      retained.push(block)
    }
  }

  flush()
  return retained
}

function deduplicateAdjacent(blocks) {
  const retained = []
  const fingerprint = block => block.trim().replace(/\s+/gu, ' ')

  for (const block of blocks) {
    if (retained.length && fingerprint(retained.at(-1)) === fingerprint(block)) {
      retained[retained.length - 1] = block
    } else retained.push(block)
  }

  return retained
}

function packBlocks(blocks) {
  const packed = []
  let current = []
  let currentLength = 0

  const flush = () => {
    if (current.length) packed.push(current.join('\n\n'))
    current = []
    currentLength = 0
  }

  for (const block of blocks) {
    const separatorLength = current.length ? 2 : 0
    const nextLength = currentLength + separatorLength + codePointLength(block)
    if (current.length && (current.length >= MAX_PACKED_BLOCKS || nextLength > MAX_PACKED_CODE_POINTS)) flush()
    if (current.length) currentLength += 2
    current.push(block)
    currentLength += codePointLength(block)
  }

  flush()
  return packed
}

export function toTranscriptBlocks(text) {
  const meaningful = baseTranscriptBlocks(text).filter(block => !isKnownTransient(block))
  return packBlocks(deduplicateAdjacent(suppressShortBursts(meaningful)))
}
