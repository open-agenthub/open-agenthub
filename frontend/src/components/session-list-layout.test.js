// @vitest-environment happy-dom
import { afterEach, describe, expect, it } from 'vitest'
import { mount } from '@vue/test-utils'
import SessionList from './SessionList.vue'
import source from './SessionList.vue?raw'

// Vitest does not load component CSS, and happy-dom does no layout, so the regression from #179
// (an invisible action strip that still took ~104px of every sidebar row) would pass any
// DOM-only test. The component's own stylesheet is injected unscoped instead — only this
// component is mounted, so its selectors match exactly what they match in the app — and the
// test asserts the property that broke: whatever is hidden must not take part in the row's
// flex layout, and must not take clicks meant for the title.
const css = source.match(/<style scoped>([\s\S]*?)<\/style>/)[1]

const owner = { phase: 'Running', mode: 'Interactive', agent: 'Codex', accessRole: 'Owner', outdatedImage: true, canResume: true }
const sessions = [
  { ...owner, id: 'a', title: 'Active session' },
  { ...owner, id: 'b', title: 'A very long session title that has to keep the row width for itself', failureReason: 'Clone failed' }
]

let style
afterEach(() => { style?.remove(); document.body.innerHTML = '' })

function render() {
  style = document.createElement('style')
  style.textContent = css
  document.head.append(style)
  return mount(SessionList, { props: { sessions, active: 'a' }, attachTo: document.body })
}

describe('session list row layout', () => {
  it('keeps the row actions out of the flex flow, so the title gets the row width', () => {
    const wrapper = render()
    const rows = wrapper.findAll('li')
    expect(rows).toHaveLength(2)
    for (const row of rows) {
      const actions = getComputedStyle(row.get('.acts').element)
      expect(actions.position).toBe('absolute')
      expect(getComputedStyle(row.element).position).toBe('relative')
      expect(getComputedStyle(row.get('.info').element).flexGrow).toBe('1')
    }
    wrapper.unmount()
  })

  it('lets clicks through actions while they are hidden', () => {
    const wrapper = render()
    const hidden = getComputedStyle(wrapper.findAll('li')[1].get('.acts').element)
    expect(hidden.opacity).toBe('0')
    expect(hidden.pointerEvents).toBe('none')
    wrapper.unmount()
  })
})
