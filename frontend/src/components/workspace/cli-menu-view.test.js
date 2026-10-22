// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { defineComponent, onMounted } from 'vue'
import { flushPromises, mount } from '@vue/test-utils'
import TerminalView from '../TerminalView.vue'

// #112: the agent CLI's own menus show up in the Workspace and can be answered there.
const mocks = vi.hoisted(() => ({
  api: {
    getConversation: vi.fn(), listPermissions: vi.fn().mockResolvedValue([]),
    listSessionMessages: vi.fn().mockResolvedValue([]), decidePermission: vi.fn()
  },
  sendKeys: vi.fn(), submit: vi.fn(), menu: null
}))
vi.mock('../../api.js', () => ({ api: mocks.api, getSharedConversation: vi.fn() }))
vi.mock('../../lib/mermaid.js', () => ({ renderMermaidBlocks: vi.fn() }))

const session = { id: 's1', agent: 'Claude', title: 'Work', phase: 'Running', mode: 'Interactive', accessRole: 'Owner' }
const login = {
  kind: 'choice', title: 'Select login method:',
  options: [
    { number: 1, label: 'Claude account with subscription', selected: true },
    { number: 2, label: 'Console account', selected: false }
  ]
}
const wrappers = []
function view() {
  const wrapper = mount(TerminalView, {
    props: { session, initialUi: 'workspace' },
    global: { stubs: {
      SessionWorkspace: { template: '<div><slot /></div>' }, ShareSessionDialog: true,
      TerminalPane: defineComponent({
        props: ['kind'], emits: ['status', 'menu'],
        setup(props, { emit, expose }) {
          expose({ submitMessage: mocks.submit, interruptAgent: vi.fn(), sendKeys: mocks.sendKeys })
          onMounted(() => {
            emit('status', 'connected')
            if (props.kind === 'agent') mocks.menu = value => emit('menu', value)
          })
          return () => null
        }
      })
    } }
  })
  wrappers.push(wrapper)
  return wrapper
}
const drawerOpen = wrapper => wrapper.get('[data-drawer-agent]').attributes('aria-expanded') === 'true'

beforeEach(() => {
  vi.clearAllMocks()
  mocks.sendKeys.mockResolvedValue(undefined)
  mocks.api.getConversation.mockResolvedValue({ source: 'native', entries: [], text: '', offset: 0, nextOffset: 0, length: 0 })
})
afterEach(() => { wrappers.splice(0).forEach(wrapper => wrapper.unmount()) })

describe('agent CLI menus in the Workspace', () => {
  it('offers choices with the terminal closed and waits for the screen to leave the menu', async () => {
    const wrapper = view()
    await flushPromises()
    expect(wrapper.find('[data-cli-menu]').exists()).toBe(false)
    expect(drawerOpen(wrapper)).toBe(false)

    mocks.menu(login)
    await flushPromises()
    expect(wrapper.get('[data-cli-menu]').text()).toContain('The agent is showing a menu')
    expect(wrapper.get('[data-cli-menu]').text()).toContain('Select login method:')
    expect(drawerOpen(wrapper)).toBe(false)

    await wrapper.get('[data-cli-option="2"]').trigger('click')
    expect(mocks.sendKeys).toHaveBeenCalledWith(['\x1b[B', '\r'])

    expect(wrapper.find('[data-cli-menu]').exists()).toBe(true)
    mocks.menu(null)
    await flushPromises()
    expect(wrapper.find('[data-cli-menu]').exists()).toBe(false)
    expect(drawerOpen(wrapper)).toBe(false)
  })

  it('cancels with Esc', async () => {
    const wrapper = view()
    await flushPromises()
    mocks.menu(login)
    await flushPromises()
    await wrapper.get('[data-cli-cancel]').trigger('click')
    expect(mocks.sendKeys).toHaveBeenCalledWith(['\x1b'])
  })

  it('leaves the explicitly opened drawer open when the menu goes', async () => {
    const wrapper = view()
    await flushPromises()
    mocks.menu(login)
    await flushPromises()
    await wrapper.get('[data-cli-open-terminal]').trigger('click')
    expect(drawerOpen(wrapper)).toBe(true)
    mocks.menu(null)
    await flushPromises()
    expect(drawerOpen(wrapper)).toBe(true)
  })

  it('refuses a composer message while a menu is open and keeps the draft, then sends once it is gone', async () => {
    mocks.submit.mockResolvedValue(undefined)
    const wrapper = view()
    await flushPromises()
    mocks.menu(login)
    await flushPromises()
    await wrapper.get('[data-workspace-composer] textarea').setValue('Fix the tests')
    await wrapper.get('[data-workspace-composer]').trigger('submit')
    await flushPromises()
    expect(mocks.submit).not.toHaveBeenCalled()
    expect(wrapper.get('[data-workspace-composer] [role=alert]').text()).toContain('showing a menu')
    expect(wrapper.get('[data-workspace-composer] textarea').element.value).toBe('Fix the tests')

    mocks.menu(null)
    await flushPromises()
    expect(wrapper.find('[data-workspace-composer] [role=alert]').exists()).toBe(false)
    await wrapper.get('[data-workspace-composer]').trigger('submit')
    await flushPromises()
    expect(mocks.submit).toHaveBeenCalledWith('Fix the tests', undefined)
  })

  it('points at the terminal for a prompt that wants typed input, without buttons', async () => {
    const wrapper = view()
    await flushPromises()
    mocks.menu({ kind: 'input', title: 'Login', options: [] })
    await flushPromises()
    expect(wrapper.get('[data-cli-menu]').text()).toContain('waiting for input')
    expect(wrapper.find('[data-cli-option]').exists()).toBe(false)
    expect(drawerOpen(wrapper)).toBe(false)
    await wrapper.get('[data-cli-open-terminal]').trigger('click')
    expect(drawerOpen(wrapper)).toBe(true)
  })
})


describe('CLI sign-in in Workspace', () => {
  it('sends a secret directly to the PTY, clears the field, and reports the CLI outcome', async () => {
    const wrapper = view()
    await flushPromises()
    mocks.menu({ kind: 'signin', mode: 'code', status: 'waiting', url: 'https://example.com/authorize', options: [] })
    await flushPromises()
    const link = wrapper.get('[data-cli-signin-open]')
    expect(link.attributes('target')).toBe('_blank')
    expect(link.attributes('rel')).toContain('noopener')
    await wrapper.get('[data-cli-signin-code]').setValue('example-authorization-code')
    await wrapper.get('[data-cli-signin] form').trigger('submit')
    await flushPromises()
    expect(mocks.sendKeys).toHaveBeenCalledWith(['\x1b[200~example-authorization-code\x1b[201~', '\r'])
    expect(mocks.submit).not.toHaveBeenCalled()
    expect(wrapper.get('[data-cli-signin-code]').element.value).toBe('')
    expect(drawerOpen(wrapper)).toBe(false)
    mocks.menu({ kind: 'signin', mode: 'outcome', status: 'failed', retry: true, options: [] })
    await flushPromises()
    expect(wrapper.get('[data-cli-signin]').text()).toContain('Sign-in failed')
    await wrapper.get('[data-cli-signin] button').trigger('click')
    expect(mocks.sendKeys).toHaveBeenLastCalledWith(['\r'])
  })
  it('rejects control characters and clears secrets when a new login screen appears', async () => {
    const wrapper = view()
    await flushPromises()
    mocks.menu({ kind: 'signin', mode: 'code', status: 'waiting', url: 'https://example.com/authorize', options: [] })
    await flushPromises()
    await wrapper.get('[data-cli-signin-code]').setValue('example\x1b[31m')
    await wrapper.get('[data-cli-signin] form').trigger('submit')
    await flushPromises()
    expect(mocks.sendKeys).not.toHaveBeenCalled()
    expect(wrapper.get('[data-cli-signin] [role=alert]').text()).toContain('single code')
    mocks.menu({ kind: 'signin', mode: 'code', status: 'waiting', url: 'https://example.com/authorize?new=true', options: [] })
    await flushPromises()
    expect(wrapper.get('[data-cli-signin-code]').element.value).toBe('')
  })
})
