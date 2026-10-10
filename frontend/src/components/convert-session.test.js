// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import TerminalView from './TerminalView.vue'
import SessionsView from './SessionsView.vue'
import ConvertSessionCard from './ConvertSessionCard.vue'
import { conversionErrorText, conversionHint, convertedLabel } from '../lib/conversion.js'

const mocks = vi.hoisted(() => ({
  api: {
    getTranscript: vi.fn().mockResolvedValue(''),
    getConversation: vi.fn().mockResolvedValue({ source: 'scrollback', entries: [], text: '', nextOffset: 0, length: 0 }),
    listPermissions: vi.fn().mockResolvedValue([]),
    listSessionMessages: vi.fn().mockResolvedValue([]),
    listProviderAccounts: vi.fn().mockResolvedValue({}),
    convertSession: vi.fn()
  },
  config: { gitEnabled: false }
}))

vi.mock('../api.js', () => ({
  api: mocks.api,
  config: mocks.config,
  getSharedTranscript: vi.fn().mockResolvedValue(''),
  getSharedConversation: vi.fn().mockResolvedValue({ source: 'scrollback', entries: [], text: '', nextOffset: 0, length: 0 })
}))

// A finished autonomous run, as the backend reports it: canConvertToInteractive is the
// backend's own predicate, the card never re-derives it from mode and phase.
const finished = (extra = {}) => ({
  id: 's1', title: 'Nightly triage', phase: 'Succeeded', mode: 'Autonomous', agent: 'Claude',
  canResume: true, canConvertToInteractive: true, ...extra
})

function mountTerminal(session, props = {}) {
  return mount(TerminalView, {
    props: { session, ...props },
    global: { stubs: { TerminalPane: true, ChatPane: true, ShareSessionDialog: true, SessionWorkspace: true } }
  })
}

describe('continuing an autonomous session interactively', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mocks.api.convertSession.mockResolvedValue({ id: 's1', mode: 'Interactive' })
  })
  afterEach(() => vi.useRealTimers())

  it('offers the card only on a stopped autonomous session the viewer manages', () => {
    expect(mountTerminal(finished()).find('[data-convert-card]').exists()).toBe(true)
    // Still running: the backend would answer 409, so the flag is false and no card appears.
    expect(mountTerminal(finished({ phase: 'Running', canResume: false, canConvertToInteractive: false }))
      .find('[data-convert-card]').exists()).toBe(false)
    // Already interactive.
    expect(mountTerminal(finished({ mode: 'Interactive', canConvertToInteractive: false }))
      .find('[data-convert-card]').exists()).toBe(false)
    // A viewer of a shared session cannot manage it.
    expect(mountTerminal(finished({ accessRole: 'Viewer' })).find('[data-convert-card]').exists()).toBe(false)
    expect(mountTerminal(finished(), { sharedToken: 'tok' }).find('[data-convert-card]').exists()).toBe(false)
  })

  it('keeps the plain Resume next to the card, since resuming as autonomous stays valid', () => {
    const wrapper = mountTerminal(finished())
    expect(wrapper.text()).toContain('▶ Resume')
    expect(wrapper.find('[data-convert-card]').exists()).toBe(true)
  })

  it('opens a terminal with auto-approve off by default and reports the conversion', async () => {
    const wrapper = mountTerminal(finished())
    await wrapper.find('[data-convert-terminal]').trigger('click')
    await flushPromises()

    expect(mocks.api.convertSession).toHaveBeenCalledWith('s1', { mode: 'interactive', uiMode: 'terminal', autoApprove: false })
    expect(wrapper.emitted('converted')).toEqual([['s1']])
  })

  it('keeps auto-approve when the checkbox is ticked', async () => {
    const wrapper = mountTerminal(finished())
    await wrapper.find('[data-convert-keep-auto-approve]').setValue(true)
    await wrapper.find('[data-convert-terminal]').trigger('click')
    await flushPromises()

    expect(mocks.api.convertSession).toHaveBeenCalledWith('s1', expect.objectContaining({ autoApprove: true }))
  })

  it('offers the chat pane for Claude only, and asks for it as uiMode chat', async () => {
    const claude = mountTerminal(finished({ agent: 'Claude' }))
    expect(claude.find('[data-convert-chat]').exists()).toBe(true)
    await claude.find('[data-convert-chat]').trigger('click')
    await flushPromises()
    expect(mocks.api.convertSession).toHaveBeenCalledWith('s1', expect.objectContaining({ uiMode: 'chat' }))

    for (const agent of ['Codex', 'Cursor', 'OpenClaw']) {
      const other = mountTerminal(finished({ agent }))
      expect(other.find('[data-convert-card]').exists()).toBe(true)
      expect(other.find('[data-convert-chat]').exists()).toBe(false)
    }
  })

  it('says per agent whether the conversation survives', () => {
    expect(mountTerminal(finished({ agent: 'Claude' })).find('[data-convert-hint]').text()).toContain('keeps the conversation')
    expect(mountTerminal(finished({ agent: 'Codex' })).find('[data-convert-hint]').text()).toContain('keeps the conversation')
    expect(mountTerminal(finished({ agent: 'Cursor' })).find('[data-convert-hint]').text()).toContain('new conversation in the same workspace')
    expect(mountTerminal(finished({ agent: 'OpenClaw' })).find('[data-convert-hint]').text()).toContain('new conversation in the same workspace')
  })

  it('shows a refusal inline instead of a popup and stays on the card', async () => {
    const error = new Error('409 {"error":"Pause the session or wait for it to finish before converting it."}')
    error.status = 409
    mocks.api.convertSession.mockRejectedValueOnce(error)
    // happy-dom has no alert; a popup would be the forbidden way to show this.
    const alert = vi.fn()
    globalThis.alert = alert

    const wrapper = mountTerminal(finished())
    await wrapper.find('[data-convert-terminal]').trigger('click')
    await flushPromises()

    expect(wrapper.find('[data-convert-error]').text()).toBe('Pause the session or wait for it to finish before converting it.')
    expect(wrapper.emitted('converted')).toBeUndefined()
    expect(wrapper.find('[data-convert-terminal]').attributes('disabled')).toBeUndefined()
    expect(alert).not.toHaveBeenCalled()
  })

  it('marks a converted session in the header without hiding its mode', () => {
    const wrapper = mountTerminal({
      id: 's1', title: 'Nightly triage', phase: 'Running', mode: 'Interactive', agent: 'Claude',
      convertedFrom: 'Autonomous', canConvertToInteractive: false
    })
    expect(wrapper.find('[data-converted-from]').text()).toBe('(converted from autonomous)')
    expect(wrapper.find('.meta').text()).toContain('Interactive')
    expect(mountTerminal(finished()).find('[data-converted-from]').exists()).toBe(false)
  })
})

describe('the sessions list', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mocks.api.convertSession.mockResolvedValue({ id: 's1', mode: 'Interactive' })
  })

  const sessions = [
    finished(),
    { id: 's2', title: 'Live run', phase: 'Running', mode: 'Autonomous', agent: 'Codex', canConvertToInteractive: false },
    { id: 's3', title: 'Chat', phase: 'Paused', mode: 'Interactive', canResume: true, canConvertToInteractive: false }
  ]

  it('shows the action only on convertible rows and unfolds the card under that row', async () => {
    const wrapper = mount(SessionsView, { props: { sessions, projects: [] } })
    const actions = wrapper.findAll('[data-convert-action]')
    expect(actions).toHaveLength(1)
    expect(wrapper.find('[data-convert-card]').exists()).toBe(false)

    await actions[0].trigger('click')
    expect(wrapper.find('[data-convert-card]').exists()).toBe(true)
    // Clicking the action must not open the session itself.
    expect(wrapper.emitted('select')).toBeUndefined()

    await wrapper.find('[data-convert-cancel]').trigger('click')
    expect(wrapper.find('[data-convert-card]').exists()).toBe(false)
  })

  it('converts from the row and hands the id up so the app can open it', async () => {
    const wrapper = mount(SessionsView, { props: { sessions, projects: [] } })
    await wrapper.find('[data-convert-action]').trigger('click')
    await wrapper.find('[data-convert-terminal]').trigger('click')
    await flushPromises()

    expect(mocks.api.convertSession).toHaveBeenCalledWith('s1', { mode: 'interactive', uiMode: 'terminal', autoApprove: false })
    expect(wrapper.emitted('converted')).toEqual([['s1']])
    expect(wrapper.find('[data-convert-card]').exists()).toBe(false)
  })
})

describe('the card on its own', () => {
  it('resets its choices when it is pointed at another session', async () => {
    const wrapper = mount(ConvertSessionCard, { props: { session: finished() } })
    await wrapper.find('[data-convert-keep-auto-approve]').setValue(true)
    await wrapper.setProps({ session: finished({ id: 's9' }) })
    expect(wrapper.find('[data-convert-keep-auto-approve]').element.checked).toBe(false)
  })

  it('names a paused run as paused, not finished', () => {
    expect(mount(ConvertSessionCard, { props: { session: finished({ phase: 'Paused' }) } }).text()).toContain('is paused')
    expect(mount(ConvertSessionCard, { props: { session: finished() } }).text()).toContain('has finished')
  })
})

describe('conversion helpers', () => {
  it('unwraps the {error} body api.js folds into the message', () => {
    expect(conversionErrorText(new Error('409 {"error":"Pause first."}'))).toBe('Pause first.')
    expect(conversionErrorText(new Error('400 plain text'))).toBe('plain text')
    expect(conversionErrorText(new Error(''))).toBe('The session could not be converted.')
  })

  it('labels the converted-from mode in lower case and only when set', () => {
    expect(convertedLabel({ convertedFrom: 'Autonomous' })).toBe('converted from autonomous')
    expect(convertedLabel({})).toBeNull()
  })

  it('has a hint for an unknown agent too', () => {
    expect(conversionHint(undefined)).toContain('new conversation')
  })
})
