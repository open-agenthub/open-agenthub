// @vitest-environment happy-dom
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import GroupsPane from './GroupsPane.vue'

const mocks = vi.hoisted(() => ({
  api: {
    librarySettings: vi.fn(), setLibrarySettings: vi.fn()
  }
}))
vi.mock('../api.js', () => ({ api: mocks.api }))

const err = (status, msg) => Object.assign(new Error(msg), { status })

describe('GroupsPane', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mocks.api.librarySettings.mockResolvedValue({ userSkillPublishing: false })
    mocks.api.setLibrarySettings.mockResolvedValue({ userSkillPublishing: true })
  })

  it('renders the license gate on 402 instead of controls', async () => {
    mocks.api.librarySettings.mockRejectedValue(err(402, '402 license required'))
    const wrapper = mount(GroupsPane)
    await flushPromises()
    const locked = wrapper.get('[data-library-locked]')
    expect(locked.attributes('data-license-gate')).toBeDefined()
    expect(locked.text()).toContain('Enterprise feature')
    expect(locked.text()).toContain('Publishing skills')
    expect(wrapper.find('[data-skill-publishing-toggle]').exists()).toBe(false)
  })

  it('swaps the controls for the gate when saving is refused with 402', async () => {
    mocks.api.setLibrarySettings.mockRejectedValue(err(402, '402 license required'))
    const wrapper = mount(GroupsPane)
    await flushPromises()
    await wrapper.get('[data-skill-publishing-toggle]').setValue(true)
    await flushPromises()
    expect(wrapper.find('[data-license-gate]').exists()).toBe(true)
    expect(wrapper.find('[data-skill-publishing-toggle]').exists()).toBe(false)
  })

  it('saves the user skill publishing toggle', async () => {
    const wrapper = mount(GroupsPane)
    await flushPromises()
    await wrapper.get('[data-skill-publishing-toggle]').setValue(true)
    await flushPromises()
    expect(mocks.api.setLibrarySettings).toHaveBeenCalledWith({ userSkillPublishing: true })
    expect(wrapper.get('[data-publishing-msg]').text()).toContain('Saved ✓')
  })

  it('reverts the publishing toggle when saving fails', async () => {
    mocks.api.setLibrarySettings.mockRejectedValue(err(500, '500 boom'))
    const wrapper = mount(GroupsPane)
    await flushPromises()
    const toggle = wrapper.get('[data-skill-publishing-toggle]')
    await toggle.setValue(true)
    await flushPromises()
    expect(toggle.element.checked).toBe(false)
  })
})
