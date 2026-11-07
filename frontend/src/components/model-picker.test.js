// @vitest-environment happy-dom
import { mount, flushPromises } from '@vue/test-utils'
import { describe, it, expect, vi } from 'vitest'
import ModelPicker from './ModelPicker.vue'

const listed = { freshness: 'fresh', fetchedAt: new Date().toISOString(),
  catalog: { state: 'ok', verified: true, source: 'cli', models: [
    { id: 'gpt-6-luna', label: 'Luna', capabilities: { reasoningEfforts: ['low', 'high'] } },
    { id: 'gpt-6.1-sol', isDefault: true }] } }

function client(overrides = {}) {
  return { modelCatalog: vi.fn().mockResolvedValue(listed), refreshModelCatalog: vi.fn().mockResolvedValue(listed), ...overrides }
}

describe('model picker', () => {
  it('lists the account\'s models, chooses one by click and keeps free text', async () => {
    const api = client()
    const wrapper = mount(ModelPicker, { props: { agent: 'Codex', authMode: 'Subscription', credentialId: 'acct-1', modelValue: '', client: api } })
    await flushPromises()
    expect(api.modelCatalog).toHaveBeenCalledWith({ agent: 'Codex', authMode: 'Subscription', credentialId: 'acct-1' })
    expect(wrapper.findAll('[data-model-option]').map(b => b.attributes('data-model-option'))).toEqual(['gpt-6.1-sol', 'gpt-6-luna'])
    expect(wrapper.get('[data-model-option="gpt-6-luna"]').text()).toContain('effort low/high')
    await wrapper.get('[data-model-option="gpt-6-luna"]').trigger('click')
    expect(wrapper.emitted('update:modelValue').at(-1)).toEqual(['gpt-6-luna'])
    await wrapper.get('[data-model-input]').setValue('typed-id')
    expect(wrapper.emitted('update:modelValue').at(-1)).toEqual(['typed-id'])
    await wrapper.setProps({ modelValue: 'typed-id' })
    expect(wrapper.get('[data-model-unlisted]').text()).toMatch(/kept as typed/)
    // Nothing the picker does on its own changes the choice.
    expect(wrapper.emitted('update:modelValue')).toHaveLength(2)
  })

  it('asks again for another account and shows an unknown list as a hint, not a block', async () => {
    const api = client({ modelCatalog: vi.fn().mockResolvedValue({ freshness: 'unknown' }) })
    const wrapper = mount(ModelPicker, { props: { agent: 'Claude', authMode: 'Subscription', credentialId: 'a', client: api } })
    await flushPromises()
    await wrapper.setProps({ credentialId: 'b' })
    await flushPromises()
    expect(api.modelCatalog.mock.calls.map(call => call[0].credentialId)).toEqual(['a', 'b'])
    expect(wrapper.get('[data-model-status]').text()).toMatch(/No list for this account yet/)
    expect(wrapper.get('[data-model-input]').element.disabled).toBe(false)
  })

  it('refreshes through a running session and says when there is none', async () => {
    const error = Object.assign(new Error('409'), { status: 409 })
    const api = client({ refreshModelCatalog: vi.fn().mockRejectedValue(error) })
    const wrapper = mount(ModelPicker, { props: { agent: 'Codex', authMode: 'Subscription', credentialId: 'a', client: api } })
    await flushPromises()
    await wrapper.get('[data-model-refresh]').trigger('click')
    await flushPromises()
    expect(api.refreshModelCatalog).toHaveBeenCalledWith({ agent: 'Codex', authMode: 'Subscription', credentialId: 'a' })
    expect(wrapper.get('[data-model-notice]').text()).toMatch(/No running session uses this account/)
    // The last known list stays.
    expect(wrapper.findAll('[data-model-option]')).toHaveLength(2)
  })

  it('reports a refused account list as such', async () => {
    const api = client({ modelCatalog: vi.fn().mockResolvedValue({ ...listed, catalog: { ...listed.catalog, state: 'fallback', verified: false } }) })
    const wrapper = mount(ModelPicker, { props: { agent: 'Codex', authMode: 'Subscription', client: api } })
    await flushPromises()
    expect(wrapper.get('[data-model-status]').classes()).toContain('warn')
    expect(wrapper.get('[data-model-status]').text()).toMatch(/built-in models/)
  })
})
