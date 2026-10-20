// @vitest-environment happy-dom
import { beforeEach, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { api } from '../api.js'
import SessionWorkspace from './SessionWorkspace.vue'

vi.mock('../api.js', async importOriginal => {
  const actual = await importOriginal()
  return {
    ...actual,
    api: {
      ...actual.api,
      sessionFileCapabilities: vi.fn().mockResolvedValue({ limits: { presentationPollMilliseconds: 60_000 } }),
      listSessionFiles: vi.fn().mockResolvedValue([]),
      getFilePresentation: vi.fn().mockResolvedValue(null),
      listSecrets: vi.fn(),
      updateSession: vi.fn()
    }
  }
})

const session = {
  id: 's1', phase: 'Running', mode: 'Interactive', repos: [], browser: { phase: 'Stopped' },
  secretGrants: [],
  effectiveSecrets: [{ id: 'p', name: 'PROJECT_KEY', delivery: 'env', provenance: 'project', inheritToChildren: true }]
}

beforeEach(() => {
  api.listSecrets.mockReset().mockResolvedValue([{ id: 'p', name: 'PROJECT_KEY' }, { id: 'g', name: 'DEPLOY_FILE' }])
  api.updateSession.mockReset().mockResolvedValue({})
})

async function mountView(props) {
  const wrapper = mount(SessionWorkspace, { props: { session, ...props }, slots: { default: '<div></div>' } })
  await flushPromises()
  return wrapper
}

it('saves a grant choice from the session view at once, without inheritance', async () => {
  const wrapper = await mountView({ canManage: true })
  expect(wrapper.get('[data-secret-provenance="p"]').text()).toBe('project')
  await wrapper.get('[data-secret-grant="g"] [data-grant-level="session"]').trigger('click')
  await flushPromises()
  expect(api.updateSession).toHaveBeenCalledWith('s1', { secretGrants: [{ secretId: 'g', inheritToChildren: false }] })
  expect(wrapper.emitted('updated')).toHaveLength(1)
  expect(wrapper.get('[data-secret-grant="g"] [aria-checked="true"]').attributes('data-grant-level')).toBe('session')
  wrapper.unmount()
})

it('puts the previous choice back and says why when the save is refused', async () => {
  api.updateSession.mockRejectedValue(new Error('secret_not_allowed'))
  const wrapper = await mountView({ canManage: true })
  await wrapper.get('[data-secret-grant="g"] [data-grant-level="children"]').trigger('click')
  await flushPromises()
  expect(wrapper.get('[data-grant-error]').text()).toContain('secret_not_allowed')
  expect(wrapper.get('[data-secret-grant="g"] [aria-checked="true"]').attributes('data-grant-level')).toBe('none')
  expect(wrapper.emitted('updated')).toBeUndefined()
  wrapper.unmount()
})

it('keeps the read-only list for someone who cannot manage the session', async () => {
  const wrapper = await mountView({ canManage: false })
  expect(api.listSecrets).not.toHaveBeenCalled()
  expect(wrapper.find('[data-secret-grants]').exists()).toBe(false)
  expect(wrapper.get('[data-session-secrets]').text()).toContain('PROJECT_KEY · env · project')
  wrapper.unmount()
})

it('shows a scheduled session its grants without letting them change', async () => {
  const wrapper = await mountView({ canManage: true, session: { ...session, mode: 'Scheduled' } })
  expect(wrapper.get('[data-secret-grant="g"] [data-grant-level="session"]').attributes('disabled')).toBeDefined()
  expect(wrapper.text()).toContain('delete and recreate it')
  wrapper.unmount()
})
