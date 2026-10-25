// @vitest-environment happy-dom
import { mount } from '@vue/test-utils'
import { beforeEach, expect, it, vi } from 'vitest'
import WorkspaceComposer from './WorkspaceComposer.vue'
import { workspaceDrafts, workspaceQueues } from '../../lib/session-drafts.js'
beforeEach(() => { workspaceDrafts.clear(); workspaceQueues.clear() })
function composer() {
  return mount(WorkspaceComposer, { props: { sessionId: 'prediction', enabled: true,
    suggestion: 'run the tests', send: vi.fn().mockResolvedValue() } })
}
it.each(['ArrowRight', 'Tab'])('%s accepts into a draft; only a later Enter sends', async key => {
  const wrapper = composer(), input = wrapper.get('textarea'), send = wrapper.props('send')
  expect(input.element.value).toBe('')
  expect(wrapper.get('[data-composer-suggestion]').text()).toBe('run the tests')
  await input.trigger('keydown', { key: 'Enter' })
  expect(send).not.toHaveBeenCalled()
  await input.trigger('keydown', { key })
  expect(input.element.value).toBe('run the tests')
  expect(wrapper.find('[data-composer-suggestion]').exists()).toBe(false)
  expect(send).not.toHaveBeenCalled()
  await input.setValue('run the tests carefully')
  await input.trigger('keydown', { key: 'Enter' })
  expect(send).toHaveBeenCalledWith('run the tests carefully')
  wrapper.unmount()
})
it('protects drafts, dismisses on typing, and ignores modified/composing keys', async () => {
  const wrapper = composer(), input = wrapper.get('textarea')
  await input.trigger('keydown', { key: 'Tab', shiftKey: true })
  await input.trigger('keydown', { key: 'ArrowRight', isComposing: true })
  expect(input.element.value).toBe('')
  await input.setValue('my own draft')
  await wrapper.setProps({ suggestion: 'check the result' })
  expect(input.element.value).toBe('my own draft')
  expect(wrapper.find('[data-composer-suggestion]').exists()).toBe(false)
  await input.setValue('')
  expect(wrapper.find('[data-composer-suggestion]').exists()).toBe(false)
  await wrapper.setProps({ suggestion: '' })
  await wrapper.setProps({ suggestion: 'next turn prediction' })
  expect(wrapper.get('[data-composer-suggestion]').text()).toBe('next turn prediction')
  await wrapper.setProps({ enabled: false })
  expect(wrapper.find('[data-composer-suggestion]').exists()).toBe(false)
  wrapper.unmount()
})
