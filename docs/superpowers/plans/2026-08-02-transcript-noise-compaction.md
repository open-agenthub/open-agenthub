# Transcript Noise Compaction Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove terminal TUI animation/redraw noise and pack retained transcript content into substantially fewer chronological bubbles without losing isolated short commands.

**Architecture:** Extend the existing framework-free `toTranscriptBlocks(text)` pipeline with transient classification, context-sensitive short-burst suppression, adjacent redraw deduplication, and bounded bubble packing. `TerminalView` remains a presentation consumer of the helper, while stored transcripts, backend APIs, and structured chat sessions remain unchanged.

**Tech Stack:** JavaScript, Vue 3, Vitest, Vue Test Utils, Docker Desktop Kubernetes, Helm

## Global Constraints

- Raw S3/Postgres transcript data and the backend plain-text transcript contract remain unchanged.
- Structured chat sessions continue to use `ChatPane` and bypass terminal compaction.
- Isolated short commands and outputs remain visible; only runs of four or more short blocks are suppressed.
- A short block contains at most twelve Unicode code points and at most two non-empty lines.
- Packed bubbles contain at most twenty source blocks and target at most 1,200 Unicode code points; an oversized source block remains intact.
- Retained content stays in source order and is rendered only through Vue text interpolation.

---

### Task 1: Balanced terminal-noise compaction

**Files:**
- Modify: `frontend/src/lib/transcript.test.js`
- Modify: `frontend/src/lib/transcript.js`
- Modify: `frontend/src/components/transcript-view.test.js`

**Interfaces:**
- Consumes: sanitized terminal text passed to the existing `toTranscriptBlocks(text)` export.
- Produces: `toTranscriptBlocks(text): string[]`, now returning compacted, deduplicated, and bounded display bubbles.

- [ ] **Step 1: Replace the helper fixtures with failing behavioral coverage**

Use this complete test suite in `frontend/src/lib/transcript.test.js`:

```js
import { describe, expect, it } from 'vitest'
import { toTranscriptBlocks } from './transcript.js'

const separated = (...blocks) => blocks.join('\n\n\n')

describe('toTranscriptBlocks', () => {
  it('forms base blocks, removes layout lines, and packs retained content', () => {
    const text = 'first line\n \n\t\nsecond line\n\nthird line'

    expect(toTranscriptBlocks(text)).toEqual([
      'first line\n\nsecond line\nthird line'
    ])
  })

  it('normalizes carriage returns and preserves whitespace on non-empty lines', () => {
    expect(toTranscriptBlocks('  alpha  \r\nbeta\rgamma \t')).toEqual([
      '  alpha  \nbeta\ngamma \t'
    ])
  })

  it('removes known spinner and elapsed-token frames', () => {
    expect(toTranscriptBlocks(separated(
      '✢',
      '✣ · ✶',
      '✻50s · ↓ 1.0k tokens)',
      'meaningful terminal output'
    ))).toEqual(['meaningful terminal output'])
  })

  it('removes a run of four short redraw fragments', () => {
    expect(toTranscriptBlocks(separated(
      'l',
      'o',
      'g',
      'i',
      'meaningful terminal output'
    ))).toEqual(['meaningful terminal output'])
  })

  it('preserves up to three short blocks and isolated digits', () => {
    expect(toTranscriptBlocks(separated(
      'ls',
      '42',
      'OK',
      'meaningful terminal output'
    ))).toEqual([
      'ls\n\n42\n\nOK\n\nmeaningful terminal output'
    ])
  })

  it('keeps the later whitespace-equivalent redraw', () => {
    expect(toTranscriptBlocks(separated(
      'first   screen state',
      ' first screen state '
    ))).toEqual([' first screen state '])
  })

  it('starts a new bubble after twenty source blocks', () => {
    const source = Array.from({ length: 21 }, (_, index) =>
      `meaningful source block ${String(index + 1).padStart(2, '0')}`)

    const result = toTranscriptBlocks(separated(...source))

    expect(result).toHaveLength(2)
    expect(result[0]).toContain('meaningful source block 01')
    expect(result[0]).toContain('meaningful source block 20')
    expect(result[0]).not.toContain('meaningful source block 21')
    expect(result[1]).toBe('meaningful source block 21')
  })

  it('keeps oversized source blocks intact and respects the size target', () => {
    const nearLimit = 'x'.repeat(1190)
    const oversized = 'y'.repeat(1201)

    expect(toTranscriptBlocks(separated(nearLimit, 'meaningful end block'))).toEqual([
      nearLimit,
      'meaningful end block'
    ])
    expect(toTranscriptBlocks(separated(oversized, 'meaningful end block'))).toEqual([
      oversized,
      'meaningful end block'
    ])
  })

  it('returns no blocks for missing, whitespace-only, or noise-only text', () => {
    expect(toTranscriptBlocks(null)).toEqual([])
    expect(toTranscriptBlocks(' \n\t\n')).toEqual([])
    expect(toTranscriptBlocks(separated('*', '·', '✶', '✻10s · ↓ 2k tokens)'))).toEqual([])
  })
})
```

- [ ] **Step 2: Run the helper tests and verify RED**

Run: `cd frontend && npm test -- src/lib/transcript.test.js`

Expected: FAIL because the current helper neither filters transient frames nor suppresses bursts, deduplicates redraws, or packs blocks.

- [ ] **Step 3: Implement the compaction pipeline**

Replace `frontend/src/lib/transcript.js` with:

```js
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
```

- [ ] **Step 4: Run the helper tests and verify GREEN**

Run: `cd frontend && npm test -- src/lib/transcript.test.js`

Expected: PASS with 9 tests.

- [ ] **Step 5: Update the component expectations to the packed UI contract**

In the owner test in `frontend/src/components/transcript-view.test.js`, replace the two-bubble assertions with:

```js
expect(wrapper.findAll('.transcript-bubble').map(item => item.find('pre').text())).toEqual([
  'first\n\nsecond'
])
expect(wrapper.findAll('.transcript-label').map(item => item.text())).toEqual(['Terminal'])
```

In the shared test, replace its two-bubble assertion with:

```js
expect(wrapper.findAll('.transcript-bubble').map(item => item.find('pre').text())).toEqual([
  'shared first\n\nshared second'
])
```

- [ ] **Step 6: Run helper, component, and chat-mode tests together**

Run: `cd frontend && npm test -- src/lib/transcript.test.js src/components/transcript-view.test.js src/components/views.test.js`

Expected: PASS with compacted owner/shared bubbles, retained loading/empty behavior, and the existing chat-mode Transcript-tab exclusion.

- [ ] **Step 7: Run complete frontend verification**

Run: `cd frontend && npm test`

Expected: PASS for the complete Vitest suite.

Run: `cd frontend && npm run build`

Expected: PASS with the Vite production bundle emitted.

- [ ] **Step 8: Commit the compaction behavior**

```bash
git add frontend/src/lib/transcript.js frontend/src/lib/transcript.test.js frontend/src/components/transcript-view.test.js
git commit -m "fix: compact terminal transcript noise"
```

---

### Task 2: Live sample acceptance and Docker Desktop rollout

**Files:**
- No source files change in this task.

**Interfaces:**
- Consumes: the local development API at `http://127.0.0.1:8080`, Docker image tag `open-agenthub-dev/frontend:local`, Helm release `agenthub-dev`, and namespace `agenthub-dev`.
- Produces: measured compacted block counts for local terminal transcripts and a healthy deployed frontend serving the feature branch.

- [ ] **Step 1: Measure compaction without printing transcript content**

Run from the repository root:

```powershell
@'
import { toTranscriptBlocks } from './frontend/src/lib/transcript.js'
const headers = { 'X-AgentHub-Test-User': 'dev' }
const sessions = await (await fetch('http://127.0.0.1:8080/api/sessions', { headers })).json()
for (const session of sessions.filter(item => item.uiMode !== 'chat')) {
  const response = await fetch(`http://127.0.0.1:8080/api/sessions/${session.id}/transcript`, { headers })
  const text = await response.text()
  const compacted = toTranscriptBlocks(text)
  const standaloneNoise = compacted.filter(block => /^(?:[*·…✢✣✳✶✻✽]\s*)+$/u.test(block.trim())).length
  console.log(JSON.stringify({ id: session.id, inputCharacters: [...text].length, compactedBlocks: compacted.length, standaloneNoise }))
}
'@ | node --input-type=module -
```

Expected: the transcript previously producing 7,058 display blocks is reduced to a small bounded set, `standaloneNoise` is `0`, and no transcript content is printed.

- [ ] **Step 2: Rebuild and deploy from the feature worktree**

Run: `pwsh -NoProfile -File .\setup-dev.ps1 -NoPortForward`

Expected: all local images build, Helm upgrades `agenthub-dev`, and backend/frontend rollouts report success.

- [ ] **Step 3: Ensure the frontend port-forward is running**

If port 8080 has no listener, run:

```powershell
Start-Process kubectl -ArgumentList '-n agenthub-dev port-forward svc/agenthub-frontend 8080:80' -WindowStyle Hidden
```

- [ ] **Step 4: Verify Kubernetes and HTTP health**

Run:

```powershell
kubectl -n agenthub-dev get deployments,pods
curl.exe --retry 10 --retry-connrefused --retry-delay 1 --max-time 20 -sS -o NUL -w "frontend-http=%{http_code}`n" http://127.0.0.1:8080/
```

Expected: backend and frontend deployments are `1/1`, all control-plane pods are Ready, and `frontend-http=200`.

- [ ] **Step 5: Inspect the approved transcript visually**

Open `http://localhost:8080`, select the same terminal session, and open Transcript.

Expected: no standalone spinner/timer bubbles are visible, retained content appears in substantially fewer packed Terminal bubbles, and the page remains responsive and selectable.
