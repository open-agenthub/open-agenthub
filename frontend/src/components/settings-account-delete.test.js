// @vitest-environment happy-dom
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import SettingsView from './SettingsView.vue'

const mocks = vi.hoisted(() => ({
  api: { deleteAccount: vi.fn() },
  auth: { user: 'alice', displayName: 'Alice', email: 'alice@example.com', enabled: true, logout: vi.fn() },
  config: { gitEnabled: false }
}))
vi.mock('../api.js', () => ({ api: mocks.api, auth: mocks.auth, config: mocks.config }))

const stubs = {
  AccountDialog: true, CredentialsDialog: true, SettingsDialog: true, AdminView: true,
  AdminLimitsView: true, McpServersPane: true, SkillsPane: true, GroupsPane: true
}
const mountProfile = () => mount(SettingsView, { props: { initialTab: 'profile' }, global: { stubs } })

describe('account deletion (profile danger zone)', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mocks.api.deleteAccount.mockResolvedValue(null)
    mocks.auth.enabled = true
  })

  it('keeps the delete button disabled until the exact username is typed', async () => {
    const wrapper = mountProfile()
    const button = wrapper.get('[data-delete-account]')
    expect(button.attributes('disabled')).toBeDefined()

    await wrapper.get('[data-delete-confirm]').setValue('wrong')
    expect(wrapper.get('[data-delete-account]').attributes('disabled')).toBeDefined()

    await wrapper.get('[data-delete-confirm]').setValue('alice')
    expect(wrapper.get('[data-delete-account]').attributes('disabled')).toBeUndefined()
  })

  it('deletes the account with the confirmed username and signs out', async () => {
    const wrapper = mountProfile()
    await wrapper.get('[data-delete-confirm]').setValue('alice')
    await wrapper.get('[data-delete-account]').trigger('click')
    await flushPromises()
    expect(mocks.api.deleteAccount).toHaveBeenCalledWith('alice')
    expect(mocks.auth.logout).toHaveBeenCalled()
  })

  it('shows the backend error inline and does not sign out', async () => {
    mocks.api.deleteAccount.mockRejectedValue(new Error('503 storage unavailable'))
    const wrapper = mountProfile()
    await wrapper.get('[data-delete-confirm]').setValue('alice')
    await wrapper.get('[data-delete-account]').trigger('click')
    await flushPromises()
    expect(wrapper.get('[data-delete-error]').text()).toContain('503')
    expect(mocks.auth.logout).not.toHaveBeenCalled()
    expect(wrapper.get('[data-delete-account]').attributes('disabled')).toBeUndefined()
  })

  it('explains the irreversible scope in the danger zone', () => {
    const wrapper = mountProfile()
    const zone = wrapper.get('[data-danger-zone]')
    expect(zone.text()).toContain('cannot be undone')
    expect(zone.text()).toContain('alice')
  })
})
