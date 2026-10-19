// @vitest-environment happy-dom
import { mount } from '@vue/test-utils'
import { describe, it, expect } from 'vitest'
import SecretGrantsPicker from './SecretGrantsPicker.vue'

describe('session grant controls', () => {
  it('requires explicit inheritance and lets the owner revoke', async () => {
    const wrapper = mount(SecretGrantsPicker, { props: { options: [{ id: 'a', name: 'DEPLOY_FILE' }], modelValue: [] } })
    await wrapper.get('[data-secret-grant="a"]').setValue(true)
    expect(wrapper.emitted('update:modelValue')[0][0]).toEqual([{ secretId: 'a', inheritToChildren: false }])
    await wrapper.setProps({ modelValue: [{ secretId: 'a', inheritToChildren: false, source: 'requested' }] })
    expect(wrapper.text()).toContain('requested')
    await wrapper.get('[data-secret-inherit="a"]').setValue(true)
    expect(wrapper.emitted('update:modelValue')[1][0][0].inheritToChildren).toBe(true)
    await wrapper.get('[data-secret-grant="a"]').setValue(false)
    expect(wrapper.emitted('update:modelValue')[2][0]).toEqual([])
  })
  it('allows explicit direct owner approval of an inherited grant', async () => {
    const wrapper = mount(SecretGrantsPicker, { props: { options: [{ id: 'a', name: 'DEPLOY_FILE' }], modelValue: [{ secretId: 'a', inheritToChildren: true, source: 'inherited' }] } })
    await wrapper.get('button').trigger('click')
    expect(wrapper.emitted('update:modelValue')[0][0][0].source).toBe('granted')
    expect(wrapper.text()).toContain('independent of the parent')
  })
})
