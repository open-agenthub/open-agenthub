// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { mount } from '@vue/test-utils'
import InterfaceSettings from './InterfaceSettings.vue'
import { preferredUi, savePreferredUi } from '../lib/ui-preference.js'

beforeEach(() => localStorage.clear())
afterEach(() => { vi.restoreAllMocks(); vi.unstubAllGlobals() })

describe('preferred session interface', () => {
  it('defaults to Workspace and restores a saved preference after remount', async () => {
    expect(preferredUi()).toBe('workspace')
    const wrapper = mount(InterfaceSettings)
    await wrapper.get('input[value=terminal]').setValue(true)
    expect(preferredUi()).toBe('workspace')
    await wrapper.get('form').trigger('submit')
    expect(wrapper.get('[role=status]').text()).toContain('saved')
    wrapper.unmount()
    const restored = mount(InterfaceSettings)
    expect(restored.get('input[value=terminal]').element.checked).toBe(true)
    expect(preferredUi()).toBe('terminal')
    restored.unmount()
  })

  it('rejects unsupported values and falls back safely when storage is unavailable', () => {
    expect(() => savePreferredUi('chat')).toThrow()
    localStorage.setItem('agenthub.preferredUi', 'unsupported')
    expect(preferredUi()).toBe('workspace')
    vi.stubGlobal('localStorage', { getItem() { throw new Error('blocked') } })
    expect(preferredUi()).toBe('workspace')
  })

  it('reports a failed save without claiming the preference was persisted', async () => {
    const wrapper = mount(InterfaceSettings)
    vi.stubGlobal('localStorage', { setItem() { throw new Error('quota') } })
    await wrapper.get('form').trigger('submit')
    expect(wrapper.get('[role=alert]').text()).toContain('Could not save')
    expect(wrapper.find('[role=status]').exists()).toBe(false)
    wrapper.unmount()
  })
})
