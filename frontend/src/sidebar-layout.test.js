// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import App from './App.vue'
import { SIDEBAR_DEFAULT, SIDEBAR_MAX, SIDEBAR_MIN, clampSidebarWidth, loadSidebarLayout, saveSidebarLayout } from './lib/sidebar-layout.js'

const mocks = vi.hoisted(() => ({
  api: { listSessions: vi.fn(), listProjects: vi.fn(), adminAccess: vi.fn() },
  auth: { enabled: false, isAuthenticated: true, user: 'tester', login: vi.fn(), logout: vi.fn() },
  config: { gitEnabled: false, version: 'dev', repoUrl: '' }
}))
vi.mock('./api.js', () => ({ api: mocks.api, auth: mocks.auth, config: mocks.config }))
const stubs = { ProjectSidebar: true, TerminalView: true, SettingsView: true, AdminView: true, NewSessionDialog: true, EditSessionDialog: true,
  DuplicateSessionDialog: true, ShareSessionDialog: true, SharedSessionView: true, HomeView: true }
const KEY = 'agenthub.sidebar'

beforeEach(() => {
  localStorage.clear()
  vi.spyOn(globalThis, 'setInterval').mockReturnValue(1)
  mocks.api.listSessions.mockResolvedValue([])
  mocks.api.listProjects.mockResolvedValue([])
  mocks.api.adminAccess.mockResolvedValue({ isAdmin: false })
  history.replaceState({}, '', '/')
})
afterEach(() => { vi.restoreAllMocks(); vi.unstubAllGlobals() })

describe('sidebar layout storage', () => {
  it('falls back to the default for missing, malformed or unreadable storage', () => {
    expect(loadSidebarLayout()).toEqual({ width: SIDEBAR_DEFAULT, collapsed: false })
    localStorage.setItem(KEY, '{not json')
    expect(loadSidebarLayout()).toEqual({ width: SIDEBAR_DEFAULT, collapsed: false })
    localStorage.setItem(KEY, JSON.stringify({ width: 'wide', collapsed: 'yes' }))
    expect(loadSidebarLayout()).toEqual({ width: SIDEBAR_DEFAULT, collapsed: false })
    vi.stubGlobal('localStorage', { getItem() { throw new Error('denied') } })
    expect(loadSidebarLayout()).toEqual({ width: SIDEBAR_DEFAULT, collapsed: false })
  })

  it('clamps stored widths and survives a refusing store', () => {
    localStorage.setItem(KEY, JSON.stringify({ width: 5000, collapsed: true }))
    expect(loadSidebarLayout()).toEqual({ width: SIDEBAR_MAX, collapsed: true })
    expect(clampSidebarWidth(10)).toBe(SIDEBAR_MIN)
    vi.stubGlobal('localStorage', { setItem() { throw new Error('quota') } })
    expect(() => saveSidebarLayout({ width: 300, collapsed: false })).not.toThrow()
  })
})

describe('sidebar in the shell', () => {
  async function app() {
    const wrapper = mount(App, { global: { stubs }, attachTo: document.body })
    await flushPromises()
    return wrapper
  }
  const widthOf = wrapper => wrapper.get('.shell').element.style.getPropertyValue('--side-width')

  it('collapses and expands, and remembers it across a reload', async () => {
    let wrapper = await app()
    expect(wrapper.find('[data-sidebar-expand]').exists()).toBe(false)
    await wrapper.get('[data-sidebar-collapse]').trigger('click')
    expect(wrapper.get('.shell').classes()).toContain('side-collapsed')
    expect(JSON.parse(localStorage.getItem(KEY)).collapsed).toBe(true)
    wrapper.unmount()

    wrapper = await app()
    expect(wrapper.get('.shell').classes()).toContain('side-collapsed')
    await wrapper.get('[data-sidebar-expand]').trigger('click')
    expect(wrapper.get('.shell').classes()).not.toContain('side-collapsed')
    expect(JSON.parse(localStorage.getItem(KEY)).collapsed).toBe(false)
    wrapper.unmount()
  })

  it('resizes by dragging, stores the width once the drag ends, and restores it', async () => {
    let wrapper = await app()
    expect(widthOf(wrapper)).toBe(`${SIDEBAR_DEFAULT}px`)
    const handle = wrapper.get('[data-sidebar-resize]')
    await handle.trigger('pointerdown', { button: 0, pointerId: 1, clientX: SIDEBAR_DEFAULT })
    expect(wrapper.get('.shell').classes()).toContain('side-resizing')
    await handle.trigger('pointermove', { pointerId: 1, clientX: 330 })
    await handle.trigger('pointermove', { pointerId: 1, clientX: 360 })
    expect(widthOf(wrapper)).toBe('360px')
    expect(localStorage.getItem(KEY)).toBeNull()
    await handle.trigger('pointermove', { pointerId: 1, clientX: 2000 })
    expect(widthOf(wrapper)).toBe(`${SIDEBAR_MAX}px`)
    await handle.trigger('pointerup', { pointerId: 1, clientX: 2000 })
    expect(JSON.parse(localStorage.getItem(KEY))).toEqual({ width: SIDEBAR_MAX, collapsed: false })
    expect(wrapper.get('.shell').classes()).not.toContain('side-resizing')
    await handle.trigger('pointermove', { pointerId: 1, clientX: 100 })
    expect(widthOf(wrapper)).toBe(`${SIDEBAR_MAX}px`)
    wrapper.unmount()

    wrapper = await app()
    expect(widthOf(wrapper)).toBe(`${SIDEBAR_MAX}px`)
    expect(wrapper.get('[data-sidebar-resize]').attributes('aria-valuenow')).toBe(String(SIDEBAR_MAX))
    wrapper.unmount()
  })

  it('resizes from the keyboard and resets on double-click', async () => {
    const wrapper = await app()
    const handle = wrapper.get('[data-sidebar-resize]')
    await handle.trigger('keydown', { key: 'ArrowRight' })
    expect(widthOf(wrapper)).toBe(`${SIDEBAR_DEFAULT + 16}px`)
    await handle.trigger('keydown', { key: 'Home' })
    expect(widthOf(wrapper)).toBe(`${SIDEBAR_MIN}px`)
    expect(JSON.parse(localStorage.getItem(KEY)).width).toBe(SIDEBAR_MIN)
    await handle.trigger('dblclick')
    expect(widthOf(wrapper)).toBe(`${SIDEBAR_DEFAULT}px`)
    wrapper.unmount()
  })
})
