// @vitest-environment happy-dom
import { describe, expect, it } from 'vitest'
import { mount } from '@vue/test-utils'
import UserMultiSelect from './UserMultiSelect.vue'

const users = [
  { owner: 'alice', displayName: 'Alice A', email: 'alice@example.dev' },
  { owner: 'bob', displayName: 'Bob B', email: 'bob@example.dev' },
  { owner: 'carol', displayName: '', email: '' }
]

describe('UserMultiSelect', () => {
  it('renders selected users as chips with their display name', () => {
    const wrapper = mount(UserMultiSelect, { props: { modelValue: ['alice', 'carol'], users } })
    const chips = wrapper.findAll('[data-user-chip]')
    expect(chips).toHaveLength(2)
    expect(chips[0].text()).toContain('Alice A')
    expect(chips[1].text()).toContain('carol') // no displayName -> owner
  })

  it('filters suggestions case-insensitively on owner, display name, and email', async () => {
    const wrapper = mount(UserMultiSelect, { props: { modelValue: [], users } })
    expect(wrapper.findAll('[data-user-suggestion]')).toHaveLength(0) // nothing until typed
    await wrapper.get('input').setValue('ALICE')
    expect(wrapper.findAll('[data-user-suggestion]')).toHaveLength(1)
    await wrapper.get('input').setValue('example.dev')
    expect(wrapper.findAll('[data-user-suggestion]')).toHaveLength(2) // alice + bob via email
    await wrapper.get('input').setValue('Bob B')
    expect(wrapper.findAll('[data-user-suggestion]')).toHaveLength(1)
  })

  it('excludes already selected users from suggestions', async () => {
    const wrapper = mount(UserMultiSelect, { props: { modelValue: ['alice'], users } })
    await wrapper.get('input').setValue('a') // matches alice, carol, and emails
    const names = wrapper.findAll('[data-user-suggestion]').map(s => s.text())
    expect(names.some(n => n.includes('Alice'))).toBe(false)
  })

  it('adds a user on suggestion click and clears the query', async () => {
    const wrapper = mount(UserMultiSelect, { props: { modelValue: [], users } })
    await wrapper.get('input').setValue('bob')
    await wrapper.get('[data-user-suggestion]').trigger('click')
    expect(wrapper.emitted('update:modelValue')[0]).toEqual([['bob']])
    expect(wrapper.get('input').element.value).toBe('')
  })

  it('adds an exact match on Enter but ignores partial queries', async () => {
    const wrapper = mount(UserMultiSelect, { props: { modelValue: [], users } })
    await wrapper.get('input').setValue('ali')
    await wrapper.get('input').trigger('keydown', { key: 'Enter' })
    expect(wrapper.emitted('update:modelValue')).toBeUndefined()
    await wrapper.get('input').setValue('alice')
    await wrapper.get('input').trigger('keydown', { key: 'Enter' })
    expect(wrapper.emitted('update:modelValue')[0]).toEqual([['alice']])
  })

  it('removes a user when its chip ✕ is clicked', async () => {
    const wrapper = mount(UserMultiSelect, { props: { modelValue: ['alice', 'bob'], users } })
    await wrapper.findAll('[data-user-chip] button')[0].trigger('click')
    expect(wrapper.emitted('update:modelValue')[0]).toEqual([['bob']])
  })

  it('ignores add and remove when disabled', async () => {
    const wrapper = mount(UserMultiSelect, { props: { modelValue: ['alice'], users, disabled: true } })
    await wrapper.get('input').setValue('bob')
    const sugg = wrapper.find('[data-user-suggestion]')
    if (sugg.exists()) await sugg.trigger('click')
    await wrapper.get('[data-user-chip] button').trigger('click')
    expect(wrapper.emitted('update:modelValue')).toBeUndefined()
  })
})
