// @vitest-environment happy-dom
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import UserRolesCard from './UserRolesCard.vue'

const mocks = vi.hoisted(() => ({
  api: { adminRoles: vi.fn(), setUserRole: vi.fn(), adminRoleAudit: vi.fn() }
}))
vi.mock('../api.js', () => ({ api: mocks.api }))

const overview = (patch = {}) => ({
  bootstrap: false,
  groupMapping: true,
  valuesAdmins: ['root', 'breakglass'],
  users: [
    { owner: 'root', displayName: 'Root', role: 'admin', source: 'values', groups: [], explicitRole: null },
    { owner: 'alice', displayName: 'Alice', role: 'admin', source: 'group', groups: ['leads'], explicitRole: null },
    { owner: 'bob', displayName: 'Bob', role: 'admin', source: 'user', groups: [], explicitRole: 'admin' },
    { owner: 'carol', displayName: 'Carol', role: 'user', source: 'default', groups: [], explicitRole: null }
  ],
  ...patch
})

describe('UserRolesCard', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mocks.api.adminRoles.mockResolvedValue(overview())
    mocks.api.adminRoleAudit.mockResolvedValue([])
  })

  it('shows each user with role and source, and locks values admins', async () => {
    const wrapper = mount(UserRolesCard)
    await flushPromises()
    const root = wrapper.get('[data-role-row="root"]')
    expect(root.text()).toContain('Ee:Admins in the Helm values')
    expect(root.get('select').element.disabled).toBe(true)
    expect(wrapper.get('[data-role-row="alice"]').text()).toContain('group leads')
    expect(wrapper.get('[data-role-row="bob"] select').element.value).toBe('admin')
    expect(wrapper.get('[data-role-row="carol"] [data-role-badge]').text()).toBe('user')
    // A values admin who never signed in is still listed as one.
    expect(wrapper.text()).toContain('breakglass — Ee:Admins, not signed in yet')
  })

  it('makes a user an admin and shows the audit entry', async () => {
    const after = overview()
    after.users[3] = { ...after.users[3], role: 'admin', source: 'user', explicitRole: 'admin' }
    mocks.api.setUserRole.mockResolvedValue(after)
    mocks.api.adminRoleAudit.mockResolvedValueOnce([]).mockResolvedValueOnce([
      { at: '2026-10-17T10:00:00Z', actor: 'bob', subjectKind: 'user', subject: 'carol', oldRole: null, newRole: 'admin' }
    ])
    const wrapper = mount(UserRolesCard)
    await flushPromises()
    await wrapper.get('[data-role-row="carol"] select').setValue('admin')
    await flushPromises()
    expect(mocks.api.setUserRole).toHaveBeenCalledWith('carol', 'admin')
    expect(wrapper.get('[data-role-row="carol"] [data-role-badge]').text()).toBe('admin')
    expect(wrapper.get('[data-role-audit]').text()).toContain('bob set user carol: cleared → admin')
  })

  it('clears an explicit role with null', async () => {
    mocks.api.setUserRole.mockResolvedValue(overview())
    const wrapper = mount(UserRolesCard)
    await flushPromises()
    await wrapper.get('[data-role-row="bob"] select').setValue('')
    expect(mocks.api.setUserRole).toHaveBeenCalledWith('bob', null)
  })

  it('shows the last-admin refusal and restores the stored role', async () => {
    mocks.api.setUserRole.mockRejectedValue(Object.assign(new Error('409 {"error":"This would leave no admin. Make someone else an admin first."}'), { status: 409 }))
    const wrapper = mount(UserRolesCard)
    await flushPromises()
    await wrapper.get('[data-role-row="bob"] select').setValue('user')
    await flushPromises()
    expect(wrapper.get('[data-roles-error]').text()).toBe('This would leave no admin. Make someone else an admin first.')
    expect(wrapper.get('[data-role-row="bob"] select').element.value).toBe('admin')
  })

  it('warns while bootstrap makes everyone an admin', async () => {
    mocks.api.adminRoles.mockResolvedValue(overview({ bootstrap: true, groupMapping: false }))
    const wrapper = mount(UserRolesCard)
    await flushPromises()
    expect(wrapper.find('[data-roles-bootstrap]').exists()).toBe(true)
    expect(wrapper.text()).toContain('every signed-in user is an admin')
    expect(wrapper.text()).toContain('(Enterprise)')
  })
})
