// @vitest-environment happy-dom
import { describe, expect, it, vi } from 'vitest'
import { ref } from 'vue'
import { mount } from '@vue/test-utils'
import LicenseGate from './LicenseGate.vue'

const mountGate = (provide = {}) => mount(LicenseGate, {
  props: { feature: 'Sharing sessions with other users' },
  global: { provide }
})

describe('LicenseGate', () => {
  it('tells a non-admin to ask an administrator, without a link', () => {
    const wrapper = mountGate({ isAdmin: ref(false), openSettings: vi.fn() })
    const gate = wrapper.get('[data-license-gate]')
    expect(gate.text()).toContain('Enterprise feature')
    expect(gate.text()).toContain('Sharing sessions with other users needs an active enterprise license.')
    expect(gate.get('[data-license-hint]').text()).toContain('Ask an administrator')
    expect(gate.find('[data-license-link]').exists()).toBe(false)
    expect(gate.find('svg').exists()).toBe(true)
  })

  it('offers an admin the link to the license tab and navigates in place', async () => {
    const openSettings = vi.fn()
    const wrapper = mountGate({ isAdmin: ref(true), openSettings })
    const link = wrapper.get('[data-license-link]')
    expect(link.attributes('href')).toBe('/settings/license')
    expect(wrapper.find('[data-license-hint]').exists()).toBe(false)

    const event = new MouseEvent('click', { bubbles: true, cancelable: true })
    link.element.dispatchEvent(event)
    expect(openSettings).toHaveBeenCalledWith('license')
    // In-app navigation: the anchor's own navigation (a full reload) is suppressed.
    expect(event.defaultPrevented).toBe(true)
  })

  it('follows a later admin check through the injected ref', async () => {
    const isAdmin = ref(false)
    const wrapper = mountGate({ isAdmin, openSettings: vi.fn() })
    expect(wrapper.find('[data-license-link]').exists()).toBe(false)
    isAdmin.value = true
    await wrapper.vm.$nextTick()
    expect(wrapper.find('[data-license-link]').exists()).toBe(true)
  })

  it('reads "unknown" as "not an admin" when nothing is provided', () => {
    // A pane mounted outside App.vue (component tests, the standalone admin page) must not
    // promise a link it cannot make work.
    const wrapper = mountGate()
    expect(wrapper.find('[data-license-link]').exists()).toBe(false)
    expect(wrapper.get('[data-license-hint]').exists()).toBe(true)
  })

  it('leaves the anchor to the browser when no navigation callback is provided', () => {
    const wrapper = mountGate({ isAdmin: ref(true) })
    const link = wrapper.get('[data-license-link]')
    const event = new MouseEvent('click', { bubbles: true, cancelable: true })
    link.element.dispatchEvent(event)
    expect(event.defaultPrevented).toBe(false)
  })
})
