# Conversation Transcripts Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Render sanitized terminal transcripts as a compact chronological stream of neutral terminal bubbles without changing persisted transcript data or inventing conversation roles.

**Architecture:** A framework-free helper converts plain transcript text into ordered display blocks. `TerminalView.vue` consumes those blocks and owns only their presentation, while the existing owner/shared fetch paths and structured chat rendering remain unchanged.

**Tech Stack:** JavaScript, Vue 3 Composition API, Vitest, Vue Test Utils, scoped CSS

## Global Constraints

- Treat this as presentation-only processing; do not change S3/Postgres scrollback or the backend plain-text transcript contract.
- Do not infer user/assistant roles, timestamps, tool states, or Markdown semantics from terminal text.
- Keep chat-mode sessions on the existing structured `ChatPane` path.
- Preserve all characters on every non-empty terminal line, including leading and trailing whitespace.
- Use the existing warm dark theme variables and the 760-pixel transcript reading column.

---

### Task 1: Deterministic transcript block conversion

**Files:**
- Create: `frontend/src/lib/transcript.js`
- Create: `frontend/src/lib/transcript.test.js`

**Interfaces:**
- Consumes: `text`, which may be a string, `null`, or `undefined`.
- Produces: `toTranscriptBlocks(text): string[]`, an ordered array containing only non-empty blocks joined with `\n`.

- [ ] **Step 1: Write the failing helper tests**

```js
import { describe, expect, it } from 'vitest'
import { toTranscriptBlocks } from './transcript.js'

describe('toTranscriptBlocks', () => {
  it('turns large whitespace runs into ordered blocks and removes single blank layout lines', () => {
    const text = 'first line\n \n\t\nsecond line\n\nthird line'

    expect(toTranscriptBlocks(text)).toEqual([
      'first line',
      'second line\nthird line'
    ])
  })

  it('normalizes carriage returns to line feeds', () => {
    expect(toTranscriptBlocks('alpha\r\nbeta\rgamma')).toEqual([
      'alpha\nbeta\ngamma'
    ])
  })

  it('preserves whitespace on non-empty terminal lines', () => {
    expect(toTranscriptBlocks('  prompt  \n\n output \t')).toEqual([
      '  prompt  \n output \t'
    ])
  })

  it('returns no blocks for missing or whitespace-only text', () => {
    expect(toTranscriptBlocks(null)).toEqual([])
    expect(toTranscriptBlocks(' \n\t\n')).toEqual([])
  })
})
```

- [ ] **Step 2: Run the helper tests and verify the missing module failure**

Run: `cd frontend && npm test -- src/lib/transcript.test.js`

Expected: FAIL because `./transcript.js` does not exist.

- [ ] **Step 3: Implement the minimal line-oriented converter**

```js
export function toTranscriptBlocks(text) {
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
```

- [ ] **Step 4: Run the helper tests and verify all four behaviors pass**

Run: `cd frontend && npm test -- src/lib/transcript.test.js`

Expected: PASS with 1 test file and 4 tests.

- [ ] **Step 5: Commit the converter and its tests**

```bash
git add frontend/src/lib/transcript.js frontend/src/lib/transcript.test.js
git commit -m "feat: split terminal transcripts into blocks"
```

---

### Task 2: Neutral transcript bubble stream

**Files:**
- Create: `frontend/src/components/transcript-view.test.js`
- Modify: `frontend/src/components/TerminalView.vue:1-162`

**Interfaces:**
- Consumes: `toTranscriptBlocks(transcriptText)` from Task 1 and the existing `api.getTranscript(session.id)` / `getSharedTranscript(sharedToken)` fetch results.
- Produces: `transcriptBlocks`, a Vue computed array rendered as ordered `.transcript-bubble` items; loading and empty results render a single `.transcript-state` message.

- [ ] **Step 1: Write failing component behavior tests**

```js
// @vitest-environment happy-dom
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import TerminalView from './TerminalView.vue'

const mocks = vi.hoisted(() => ({
  api: {
    getTranscript: vi.fn(),
    listPermissions: vi.fn().mockResolvedValue([]),
    decidePermission: vi.fn()
  },
  getSharedTranscript: vi.fn()
}))

vi.mock('../api.js', () => ({
  api: mocks.api,
  getSharedTranscript: mocks.getSharedTranscript
}))

const session = {
  id: 'terminal-1',
  title: 'Terminal session',
  phase: 'Succeeded',
  mode: 'Interactive'
}

function mountView(props = {}) {
  return mount(TerminalView, {
    props: { session, ...props },
    global: {
      stubs: {
        TerminalPane: true,
        ShareSessionDialog: true,
        SessionWorkspace: { template: '<div><slot /></div>' }
      }
    }
  })
}

async function openTranscript(wrapper) {
  const button = wrapper.findAll('.tabs button').find(item => item.text() === 'Transcript')
  await button.trigger('click')
  await flushPromises()
}

describe('terminal transcript bubbles', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mocks.api.getTranscript.mockResolvedValue('')
    mocks.getSharedTranscript.mockResolvedValue('')
  })

  it('renders owner transcript blocks in source order as neutral terminal bubbles', async () => {
    mocks.api.getTranscript.mockResolvedValue('first\n \n\t\nsecond')
    const wrapper = mountView()

    await openTranscript(wrapper)

    expect(mocks.api.getTranscript).toHaveBeenCalledWith('terminal-1')
    expect(wrapper.findAll('.transcript-bubble').map(item => item.find('pre').text())).toEqual(['first', 'second'])
    expect(wrapper.findAll('.transcript-label').map(item => item.text())).toEqual(['Terminal', 'Terminal'])
  })

  it('uses the same bubble rendering for shared transcripts', async () => {
    mocks.getSharedTranscript.mockResolvedValue('shared first\n\n\nshared second')
    const wrapper = mountView({ sharedToken: 'shared-token' })

    await openTranscript(wrapper)

    expect(mocks.getSharedTranscript).toHaveBeenCalledWith('shared-token')
    expect(wrapper.findAll('.transcript-bubble').map(item => item.find('pre').text())).toEqual(['shared first', 'shared second'])
  })

  it('keeps the existing empty transcript state', async () => {
    const wrapper = mountView()

    await openTranscript(wrapper)

    expect(wrapper.find('.transcript-state').text()).toBe('[no saved transcript]')
    expect(wrapper.findAll('.transcript-bubble')).toHaveLength(0)
  })
})
```

- [ ] **Step 2: Run the component tests and verify the missing bubble failure**

Run: `cd frontend && npm test -- src/components/transcript-view.test.js`

Expected: FAIL because `TerminalView` still renders one raw `<pre>` and has no `.transcript-bubble`, `.transcript-label`, or `.transcript-state` elements.

- [ ] **Step 3: Connect the converter and render semantic transcript states**

Add the import and computed value in `TerminalView.vue`:

```js
import { toTranscriptBlocks } from '../lib/transcript.js'

const transcriptText = ref(null)
const transcriptBlocks = computed(() => toTranscriptBlocks(transcriptText.value))
```

Replace the current transcript `<div>` with:

```vue
<section v-if="activeTab === 'transcript'" class="transcript" aria-labelledby="transcript-heading">
  <div class="transcript-inner">
    <h3 id="transcript-heading">What happened so far</h3>
    <p v-if="transcriptText === null" class="transcript-state">Loading…</p>
    <p v-else-if="!transcriptBlocks.length" class="transcript-state">[no saved transcript]</p>
    <ol v-else class="transcript-list" aria-label="Terminal transcript">
      <li v-for="(block, index) in transcriptBlocks" :key="index" class="transcript-bubble">
        <span class="transcript-label">Terminal</span>
        <pre>{{ block }}</pre>
      </li>
    </ol>
  </div>
</section>
```

- [ ] **Step 4: Style compact, responsive neutral bubbles**

Replace the existing transcript `<pre>` rule and add:

```css
.transcript-list { display: flex; flex-direction: column; gap: 12px; list-style: none; margin: 0; padding: 0; }
.transcript-bubble { padding: 12px 14px 14px; background: var(--panel); border: 1px solid var(--border-2); border-left: 3px solid var(--accent); border-radius: 12px; }
.transcript-label { display: block; color: var(--muted-3); font: 700 10px/1 var(--display); letter-spacing: .08em; text-transform: uppercase; }
.transcript-bubble pre { margin: 7px 0 0; white-space: pre-wrap; overflow-wrap: anywhere; font: 13px/1.6 var(--mono); color: #c9c4bb; }
.transcript-state { margin: 0; color: var(--muted-3); font: 13px/1.6 var(--mono); }
```

- [ ] **Step 5: Run the component and existing chat-view tests**

Run: `cd frontend && npm test -- src/components/transcript-view.test.js src/components/views.test.js`

Expected: PASS, including the existing assertion that chat sessions render `ChatPane` and omit the Transcript tab.

- [ ] **Step 6: Run the complete frontend verification**

Run: `cd frontend && npm test`

Expected: PASS for the complete Vitest suite.

Run: `cd frontend && npm run build`

Expected: PASS with a production bundle emitted by Vite.

- [ ] **Step 7: Commit the transcript presentation**

```bash
git add frontend/src/components/TerminalView.vue frontend/src/components/transcript-view.test.js
git commit -m "feat: render terminal transcript bubbles"
```
