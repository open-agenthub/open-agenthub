// @vitest-environment happy-dom
import { mount, flushPromises } from '@vue/test-utils'
import { describe, it, expect, vi } from 'vitest'
import SessionModelSwitch from './SessionModelSwitch.vue'

const catalog = (overrides = {}) => ({
  freshness: 'fresh', fetchedAt: new Date().toISOString(), selected: 'gpt-6.1-sol', selectedListed: true,
  switchMode: 'live', canSwitch: true,
  catalog: { state: 'ok', verified: true, source: 'cli', models: [{ id: 'gpt-6.1-sol', isDefault: true }, { id: 'gpt-6-luna' }] },
  ...overrides
})
const session = { id: 's1', agent: 'Codex', modelName: 'gpt-6.1-sol' }

function client(overrides = {}) {
  return { sessionModels: vi.fn().mockResolvedValue(catalog()), switchSessionModel: vi.fn(), ...overrides }
}

async function opened(props) {
  const wrapper = mount(SessionModelSwitch, { props, attachTo: document.body })
  await flushPromises()
  await wrapper.get('[data-model-switch-toggle]').trigger('click')
  return wrapper
}

describe('session model switch', () => {
  it('switches live, shows the agent\'s confirmation and the new model', async () => {
    const api = client({ switchSessionModel: vi.fn().mockResolvedValue({ model: 'gpt-6-luna', mode: 'live', confirmation: '• Model changed to gpt-6-luna medium' }) })
    const wrapper = await opened({ session, client: api })
    expect(wrapper.get('[data-model-switch-mode]').text()).toMatch(/running conversation/)
    await wrapper.get('[data-model-switch-option="gpt-6-luna"]').trigger('click')
    await wrapper.get('[data-model-switch-apply]').trigger('click')
    await flushPromises()
    expect(api.switchSessionModel).toHaveBeenCalledWith('s1', 'gpt-6-luna')
    expect(wrapper.get('[data-model-switch-result]').text()).toBe('Now on gpt-6-luna. The agent said: “• Model changed to gpt-6-luna medium”')
    expect(wrapper.get('[data-model-current]').text()).toBe('gpt-6-luna')
    expect(wrapper.emitted('switched')).toHaveLength(1)
    wrapper.unmount()
  })

  it('holds the button while a turn runs, and says why', async () => {
    const api = client()
    const wrapper = await opened({ session, busy: 'turn_running', client: api })
    await wrapper.get('[data-model-switch-option="gpt-6-luna"]').trigger('click')
    expect(wrapper.get('[data-model-switch-apply]').element.disabled).toBe(true)
    expect(wrapper.get('[data-model-switch-busy]').text()).toMatch(/working/)
    // Storing for a restart switches nothing now, so a running turn does not hold it.
    api.sessionModels.mockResolvedValue(catalog({ switchMode: 'restart' }))
    const restart = await opened({ session: { ...session, id: 's2', agent: 'Cursor' }, busy: 'turn_running', client: api })
    await restart.get('[data-model-switch-input]').setValue('gpt-6')
    expect(restart.get('[data-model-switch-apply]').element.disabled).toBe(false)
    expect(restart.get('[data-model-switch-apply]').text()).toBe('Save for restart')
    expect(restart.get('[data-model-switch-mode]').text()).toMatch(/next restart/)
    wrapper.unmount(); restart.unmount()
  })

  it('reports a refusal in words and keeps the old model', async () => {
    const refusal = Object.assign(new Error('model_refused'), { status: 422, code: 'model_refused', detail: "Model 'mock-b' is unavailable" })
    const api = client({ switchSessionModel: vi.fn().mockRejectedValue(refusal) })
    const wrapper = await opened({ session, client: api })
    await wrapper.get('[data-model-switch-input]').setValue('mock-b')
    await wrapper.get('[data-model-switch-apply]').trigger('click')
    await flushPromises()
    expect(wrapper.get('[data-model-switch-result]').attributes('data-code')).toBe('model_refused')
    expect(wrapper.get('[data-model-switch-result]').text()).toMatch(/refused this model.*mock-b' is unavailable/)
    expect(wrapper.get('[data-model-current]').text()).toBe('gpt-6.1-sol')
    wrapper.unmount()
  })

  it('shows viewers the model without a way to change it', async () => {
    const api = client({ sessionModels: vi.fn().mockResolvedValue(catalog({ canSwitch: false })) })
    const wrapper = mount(SessionModelSwitch, { props: { session, client: api } })
    await flushPromises()
    expect(wrapper.find('[data-model-switch-toggle]').exists()).toBe(false)
    expect(wrapper.get('[data-model-readonly]').text()).toContain('gpt-6.1-sol')
    expect(api.switchSessionModel).not.toHaveBeenCalled()
  })

  it('refuses ids the hub would refuse before sending them', async () => {
    const api = client()
    const wrapper = await opened({ session, client: api })
    for (const id of ['two words', '-c', '']) {
      await wrapper.get('[data-model-switch-input]').setValue(id)
      expect(wrapper.get('[data-model-switch-apply]').element.disabled).toBe(true)
    }
    // The current model again is no switch.
    await wrapper.get('[data-model-switch-input]').setValue('gpt-6.1-sol')
    expect(wrapper.get('[data-model-switch-apply]').element.disabled).toBe(true)
    wrapper.unmount()
  })
})

describe('closing the picker', () => {
  it('closes on Escape wherever the focus went, and reads the list again on every open', async () => {
    const api = client()
    const wrapper = await opened({ session, client: api })
    expect(wrapper.find('[data-model-switch-pop]').exists()).toBe(true)
    document.body.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))
    await flushPromises()
    expect(wrapper.find('[data-model-switch-pop]').exists()).toBe(false)
    await wrapper.get('[data-model-switch-toggle]').trigger('click')
    await flushPromises()
    expect(api.sessionModels).toHaveBeenCalledTimes(3)
    wrapper.unmount()
  })
})
