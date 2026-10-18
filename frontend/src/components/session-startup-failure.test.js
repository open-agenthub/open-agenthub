// @vitest-environment happy-dom
import { afterEach, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import SessionList from './SessionList.vue'
import SessionsView from './SessionsView.vue'
import SessionWorkspace from './SessionWorkspace.vue'

vi.mock('../api.js', async importOriginal => {
  const actual = await importOriginal()
  return {
    ...actual,
    api: {
      ...actual.api,
      sessionFileCapabilities: vi.fn().mockResolvedValue({ limits: { presentationPollMilliseconds: 60_000 } }),
      listSessionFiles: vi.fn().mockResolvedValue([]),
      getFilePresentation: vi.fn().mockResolvedValue(null)
    }
  }
})

const failureReason = 'clone: Branch main does not exist in <repo>. The default branch is trunk.\n  Edit the session repositories.'
const session = { id: 'failed', title: 'Failed clone', phase: 'Failed', mode: 'Interactive', failureReason, repos: [], browser: { phase: 'Stopped' } }

afterEach(() => vi.unstubAllGlobals())

it.each([SessionList, SessionsView])('keeps only a short marker in session lists', component => {
  const wrapper = mount(component, { props: { sessions: [session], projects: [] } })
  expect(wrapper.get('[data-session-failure-marker]').text()).toMatch(/^Cannot start/)
  expect(wrapper.text()).not.toContain('does not exist')
  expect(wrapper.find('[data-session-failure]').exists()).toBe(false)
  wrapper.unmount()
})

it('shows the full startup failure as escaped, line-preserving text in the workspace', () => {
  const wrapper = mount(SessionWorkspace, { props: { session }, slots: { default: '<div data-terminal>terminal</div>' } })
  const text = wrapper.get('[data-session-failure]')
  expect(text.element.tagName).toBe('PRE')
  expect(text.element.textContent).toBe(failureReason)
  expect(wrapper.find('repo').exists()).toBe(false)
  expect(wrapper.find('[data-terminal]').exists()).toBe(true)
  wrapper.unmount()
})

it('copies the full startup failure', async () => {
  const writeText = vi.fn().mockResolvedValue()
  vi.stubGlobal('navigator', { ...navigator, clipboard: { writeText } })
  const wrapper = mount(SessionWorkspace, { props: { session }, slots: { default: '<div></div>' } })
  await wrapper.get('[data-session-failure-copy]').trigger('click')
  await flushPromises()
  expect(writeText).toHaveBeenCalledWith(failureReason)
  expect(wrapper.get('[data-session-failure-copy]').text()).toBe('Copied')
  wrapper.unmount()
})

it('says how to copy by hand when the clipboard is refused', async () => {
  vi.stubGlobal('navigator', { ...navigator, clipboard: { writeText: vi.fn().mockRejectedValue(new Error('denied')) } })
  const wrapper = mount(SessionWorkspace, { props: { session }, slots: { default: '<div></div>' } })
  await wrapper.get('[data-session-failure-copy]').trigger('click')
  await flushPromises()
  expect(wrapper.text()).toContain('Select the text to copy it')
  wrapper.unmount()
})

it('renders no failure panel for a session that started', () => {
  const wrapper = mount(SessionWorkspace, { props: { session: { ...session, failureReason: null } }, slots: { default: '<div></div>' } })
  expect(wrapper.find('[data-session-failure]').exists()).toBe(false)
  wrapper.unmount()
})
