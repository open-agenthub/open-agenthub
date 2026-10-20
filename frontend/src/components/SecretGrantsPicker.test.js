// @vitest-environment happy-dom
import { mount } from '@vue/test-utils'
import { describe, it, expect } from 'vitest'
import SecretGrantsPicker from './SecretGrantsPicker.vue'

const level = (wrapper, id, value) => wrapper.get(`[data-secret-grant="${id}"] [data-grant-level="${value}"]`)
const checked = (wrapper, id) => wrapper.get(`[data-secret-grant="${id}"] [aria-checked="true"]`).attributes('data-grant-level')

describe('session grant controls', () => {
  it('offers one three-way radio group per secret, not allowed by default', () => {
    const wrapper = mount(SecretGrantsPicker, { props: { options: [{ id: 'a', name: 'DEPLOY_FILE' }], modelValue: [] } })
    const group = wrapper.get('[data-secret-grant="a"]')
    expect(group.attributes('role')).toBe('radiogroup')
    expect(wrapper.get(`#${group.attributes('aria-labelledby')}`).text()).toContain('DEPLOY_FILE')
    expect(group.findAll('[role="radio"]').map(b => b.text())).toEqual(['Not allowed', 'This session', 'Also child sessions'])
    expect(checked(wrapper, 'a')).toBe('none')
    expect(group.findAll('[tabindex="0"]')).toHaveLength(1)
    expect(wrapper.find('[data-secret-inherit]').exists()).toBe(false)
  })

  it('grants without inheritance unless the owner picks child sessions, and revokes', async () => {
    const wrapper = mount(SecretGrantsPicker, { props: { options: [{ id: 'a', name: 'DEPLOY_FILE' }], modelValue: [] } })
    await level(wrapper, 'a', 'session').trigger('click')
    expect(wrapper.emitted('update:modelValue')[0][0]).toEqual([{ secretId: 'a', inheritToChildren: false }])
    await wrapper.setProps({ modelValue: [{ secretId: 'a', inheritToChildren: false, source: 'requested' }] })
    expect(wrapper.get('[data-secret-provenance="a"]').text()).toBe('requested')
    expect(checked(wrapper, 'a')).toBe('session')
    await level(wrapper, 'a', 'children').trigger('click')
    expect(wrapper.emitted('update:modelValue')[1][0]).toEqual([{ secretId: 'a', inheritToChildren: true, source: 'requested' }])
    await level(wrapper, 'a', 'none').trigger('click')
    expect(wrapper.emitted('update:modelValue')[2][0]).toEqual([])
  })

  it('a new grant straight to child sessions inherits', async () => {
    const wrapper = mount(SecretGrantsPicker, { props: { options: [{ id: 'a', name: 'A' }], modelValue: [] } })
    await level(wrapper, 'a', 'children').trigger('click')
    expect(wrapper.emitted('update:modelValue')[0][0]).toEqual([{ secretId: 'a', inheritToChildren: true }])
  })

  it('moves with arrow keys, Home and End like a radio group', async () => {
    const wrapper = mount(SecretGrantsPicker, { props: { options: [{ id: 'a', name: 'A' }], modelValue: [] }, attachTo: document.body })
    await level(wrapper, 'a', 'none').trigger('keydown', { key: 'ArrowRight' })
    expect(wrapper.emitted('update:modelValue')[0][0]).toEqual([{ secretId: 'a', inheritToChildren: false }])
    expect(document.activeElement.dataset.grantLevel).toBe('session')
    await level(wrapper, 'a', 'none').trigger('keydown', { key: 'ArrowLeft' })
    expect(wrapper.emitted('update:modelValue')[1][0]).toEqual([{ secretId: 'a', inheritToChildren: true }])
    await level(wrapper, 'a', 'none').trigger('keydown', { key: 'End' })
    expect(wrapper.emitted('update:modelValue')[2][0][0].inheritToChildren).toBe(true)
    await wrapper.setProps({ modelValue: [{ secretId: 'a', inheritToChildren: true }] })
    await level(wrapper, 'a', 'children').trigger('keydown', { key: 'Home' })
    expect(wrapper.emitted('update:modelValue')[3][0]).toEqual([])
    await level(wrapper, 'a', 'children').trigger('keydown', { key: 'Tab' })
    expect(wrapper.emitted('update:modelValue')).toHaveLength(4)
    wrapper.unmount()
  })

  it('labels a project-only secret by its provenance and stays inert when disabled', async () => {
    const wrapper = mount(SecretGrantsPicker, {
      props: { options: [{ id: 'p', name: 'PROJECT_KEY' }], modelValue: [], effective: [{ id: 'p', provenance: 'project' }], disabled: true }
    })
    expect(wrapper.get('[data-secret-provenance="p"]').text()).toBe('project')
    expect(level(wrapper, 'p', 'session').attributes('disabled')).toBeDefined()
    await level(wrapper, 'p', 'session').trigger('click')
    expect(wrapper.emitted('update:modelValue')).toBeUndefined()
  })

  it('allows explicit direct owner approval of an inherited grant', async () => {
    const wrapper = mount(SecretGrantsPicker, { props: { options: [{ id: 'a', name: 'DEPLOY_FILE' }], modelValue: [{ secretId: 'a', inheritToChildren: true, source: 'inherited' }] } })
    expect(checked(wrapper, 'a')).toBe('children')
    expect(wrapper.get('[data-secret-provenance="a"]').text()).toBe('inherited')
    await wrapper.get('.direct').trigger('click')
    expect(wrapper.emitted('update:modelValue')[0][0][0].source).toBe('granted')
    expect(wrapper.text()).toContain('independent of the parent')
  })
})
