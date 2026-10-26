// @vitest-environment happy-dom
import { beforeEach, expect, it, vi } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import NewSessionDialog from './NewSessionDialog.vue'
const { api } = vi.hoisted(() => ({ api: Object.fromEntries(['getCredentialStatus', 'listProviderAccounts', 'getAllowedAgents', 'mcpServers', 'listSecrets', 'createProject', 'createSession'].map(k => [k, vi.fn()])) }))
vi.mock('../api.js', () => ({ api, config: { gitEnabled: false } }))
beforeEach(() => {
  vi.resetAllMocks()
  api.getCredentialStatus.mockResolvedValue({})
  api.listProviderAccounts.mockResolvedValue({})
  api.getAllowedAgents.mockResolvedValue({ agents: [] })
  api.mcpServers.mockResolvedValue([])
  api.listSecrets.mockResolvedValue([])
})
async function dialog(name) {
  const w = mount(NewSessionDialog, { global: { stubs: { RepoPicker: true } } })
  await flushPromises()
  await w.get('[aria-label="Project"]').setValue('__new__')
  await w.get('#new-project-name').setValue(name)
  return w
}
it('keeps a saved project selected after failure and retries without creating another', async () => {
  api.createProject.mockResolvedValue({ id: 'p1', name: 'Research', secretIds: [] })
  api.createSession.mockRejectedValueOnce(new Error('Session limit reached')).mockResolvedValueOnce({ id: 's1' })
  const w = await dialog(' Research ')
  await w.get('[data-submit]').trigger('click'); await flushPromises()
  expect(api.createProject).toHaveBeenCalledWith({ name: 'Research', color: null })
  expect(api.createSession.mock.calls[0][0]).toMatchObject({ projectId: 'p1', secretIds: null })
  expect(w.get('[data-project-notice]').text()).toContain('stays saved')
  expect(w.get('[role="alert"]').text()).toContain('Session limit reached')
  expect(w.get('[aria-label="Project"]').element.value).toBe('p1')
  await w.get('[data-submit]').trigger('click'); await flushPromises()
  expect(api.createProject).toHaveBeenCalledTimes(1)
  expect(w.emitted('created')[0][0]).toEqual({ id: 's1' })
})
it('refuses a blank name before creating anything', async () => {
  const w = await dialog('  ')
  await w.get('[data-submit]').trigger('click'); await flushPromises()
  expect(w.get('[role="alert"]').text()).toBe('Invalid project name or color.')
  expect(api.createProject).not.toHaveBeenCalled()
  expect(api.createSession).not.toHaveBeenCalled()
})
it('shows the normal duplicate error and never starts a session', async () => {
  api.createProject.mockRejectedValue(new Error('409 A project with this name already exists.'))
  const w = await dialog('Research')
  await w.get('[data-submit]').trigger('click'); await flushPromises()
  expect(w.get('[role="alert"]').text()).toContain('A project with this name already exists.')
  expect(api.createSession).not.toHaveBeenCalled()
  expect(w.find('[data-project-notice]').exists()).toBe(false)
})
