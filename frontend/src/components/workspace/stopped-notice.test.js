// @vitest-environment happy-dom
import { describe, expect, it } from 'vitest'
import { mount } from '@vue/test-utils'
import SessionStoppedNotice from './SessionStoppedNotice.vue'
const session = { id: 's1', phase: 'Paused', accessRole: 'Owner', canResume: true }
describe('stopped session notice', () => {
  it.each(['Owner', 'Collaborator', 'Viewer'])('respects lifecycle rights for %s', async accessRole => {
    const wrapper = mount(SessionStoppedNotice, { props: { session: { ...session, accessRole } } })
    expect(wrapper.find('[data-stopped-resume]').exists()).toBe(accessRole === 'Owner')
    if (accessRole === 'Owner') {
      await wrapper.find('button').trigger('click')
      expect(wrapper.emitted('resume')).toEqual([['s1']])
    }
  })
  it('hides resume on shared links even with an owner-shaped record', () => {
    expect(mount(SessionStoppedNotice, { props: { session, sharedToken: 'view-token' } }).find('button').exists()).toBe(false)
  })
  it('follows transitions and the backend resume flag', async () => {
    const wrapper = mount(SessionStoppedNotice, { props: { session } })
    expect(wrapper.text()).toContain('Session is paused')
    await wrapper.setProps({ session: { ...session, phase: 'Running' } })
    expect(wrapper.find('[data-session-stopped]').exists()).toBe(false)
    await wrapper.setProps({ session: { ...session, phase: 'Failed', failureReason: 'Could not clone repository', canResume: false } })
    expect(wrapper.text()).toContain('Session could not start')
    expect(wrapper.find('button').exists()).toBe(false)
  })
})
