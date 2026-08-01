// @vitest-environment happy-dom
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import GroupsPane from './GroupsPane.vue'

const mocks = vi.hoisted(() => ({
  api: {
    libraryGroups: vi.fn(), createLibraryGroup: vi.fn(), deleteLibraryGroup: vi.fn(),
    setLibraryGroupMembers: vi.fn(), librarySettings: vi.fn(), setLibrarySettings: vi.fn(),
    libraryUsers: vi.fn()
  }
}))
vi.mock('../api.js', () => ({ api: mocks.api }))

const err = (status, msg) => Object.assign(new Error(msg), { status })

const groups = [
  { id: 'g1', name: 'platform-team', members: ['alice', 'bob'], createdAt: '2026-07-01' },
  { id: 'g2', name: 'qa', members: [], createdAt: '2026-07-02' }
]

describe('GroupsPane', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mocks.api.libraryGroups.mockResolvedValue(groups)
    mocks.api.librarySettings.mockResolvedValue({ userSkillPublishing: false })
    mocks.api.createLibraryGroup.mockResolvedValue({ id: 'g3', name: 'new', members: [] })
    mocks.api.setLibraryGroupMembers.mockResolvedValue({ id: 'g1', name: 'platform-team', members: ['alice', 'bob', 'carol'] })
    mocks.api.setLibrarySettings.mockResolvedValue({ userSkillPublishing: true })
    mocks.api.libraryUsers.mockResolvedValue([
      { owner: 'alice', displayName: '', email: '' },
      { owner: 'bob', displayName: '', email: '' },
      { owner: 'carol', displayName: '', email: '' }
    ])
  })

  it('renders groups with member counts and prefilled member chips', async () => {
    const wrapper = mount(GroupsPane)
    await flushPromises()
    const rows = wrapper.findAll('[data-group-row]')
    expect(rows).toHaveLength(2)
    expect(rows[0].text()).toContain('platform-team')
    expect(rows[0].text()).toContain('2 members')
    const chips = rows[0].findAll('[data-group-members] [data-user-chip]')
    expect(chips.map(c => c.text().replace('✕', '').trim())).toEqual(['alice', 'bob'])
  })

  it('creates a group by name', async () => {
    const wrapper = mount(GroupsPane)
    await flushPromises()
    await wrapper.get('[data-group-name]').setValue('new-team')
    await wrapper.get('[data-group-create]').trigger('click')
    await flushPromises()
    expect(mocks.api.createLibraryGroup).toHaveBeenCalledWith({ name: 'new-team' })
    expect(mocks.api.libraryGroups).toHaveBeenCalledTimes(2) // reloaded after create
  })

  it('shows duplicate-name errors inline', async () => {
    mocks.api.createLibraryGroup.mockRejectedValue(err(400, '400 group exists'))
    const wrapper = mount(GroupsPane)
    await flushPromises()
    await wrapper.get('[data-group-name]').setValue('platform-team')
    await wrapper.get('[data-group-create]').trigger('click')
    await flushPromises()
    expect(wrapper.get('[data-group-create-error]').text()).toContain('group exists')
  })

  it('saves the member list picked via the multi-select and shows inline confirmation', async () => {
    const wrapper = mount(GroupsPane)
    await flushPromises()
    const row = wrapper.findAll('[data-group-row]')[0]
    await row.get('[data-group-members] input').setValue('carol')
    await row.get('[data-user-suggestion]').trigger('click')
    await row.get('[data-group-save-members]').trigger('click')
    await flushPromises()
    expect(mocks.api.setLibraryGroupMembers).toHaveBeenCalledWith('g1', { members: ['alice', 'bob', 'carol'] })
    expect(wrapper.get('[data-group-members-msg]').text()).toContain('Saved ✓')
  })

  it('falls back to a plain member input when the user list is unavailable', async () => {
    mocks.api.libraryUsers.mockRejectedValue(err(500, '500 unavailable'))
    const wrapper = mount(GroupsPane)
    await flushPromises()
    const row = wrapper.findAll('[data-group-row]')[0]
    expect(row.get('[data-group-members-fallback]').element.value).toBe('alice, bob')
    await row.get('[data-group-members-fallback]').setValue('alice, bob, carol')
    await row.get('[data-group-save-members]').trigger('click')
    await flushPromises()
    expect(mocks.api.setLibraryGroupMembers).toHaveBeenCalledWith('g1', { members: ['alice', 'bob', 'carol'] })
  })

  it('shows unknown-user errors (400) inline on the group row', async () => {
    mocks.api.libraryUsers.mockRejectedValue(err(500, '500 unavailable'))
    mocks.api.setLibraryGroupMembers.mockRejectedValue(err(400, '400 unknown user: zed'))
    const wrapper = mount(GroupsPane)
    await flushPromises()
    const row = wrapper.findAll('[data-group-row]')[0]
    await row.get('[data-group-members-fallback]').setValue('zed')
    await row.get('[data-group-save-members]').trigger('click')
    await flushPromises()
    expect(wrapper.get('[data-group-members-msg]').text()).toContain('unknown user')
  })

  it('renders the locked enterprise card on 402 instead of controls', async () => {
    mocks.api.libraryGroups.mockRejectedValue(err(402, '402 license required'))
    mocks.api.librarySettings.mockRejectedValue(err(402, '402 license required'))
    const wrapper = mount(GroupsPane)
    await flushPromises()
    const locked = wrapper.get('[data-library-locked]')
    expect(locked.text()).toContain('enterprise feature')
    expect(locked.text()).toContain('License')
    expect(wrapper.find('[data-group-create]').exists()).toBe(false)
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

  it('deletes a group after confirm', async () => {
    vi.spyOn(window, 'confirm').mockReturnValue(true)
    mocks.api.deleteLibraryGroup.mockResolvedValue(null)
    const wrapper = mount(GroupsPane)
    await flushPromises()
    await wrapper.findAll('[data-group-delete]')[1].trigger('click')
    await flushPromises()
    expect(mocks.api.deleteLibraryGroup).toHaveBeenCalledWith('g2')
    vi.restoreAllMocks()
  })
})
