// @vitest-environment happy-dom
import { expect, it } from 'vitest'
import { mount } from '@vue/test-utils'
import SessionList from './SessionList.vue'
import SessionsView from './SessionsView.vue'

it.each([SessionList, SessionsView])('shows the startup remedy as escaped text in session lists', component => {
  const failureReason = 'clone: Branch main does not exist in <repo>. The default branch is instagram. Edit the session repositories.'
  const session = { id: 'failed', title: 'Failed clone', phase: 'Failed', mode: 'Interactive', failureReason, repos: [] }
  const wrapper = mount(component, { props: { sessions: [session], projects: [] } })
  expect(wrapper.get('[data-session-failure]').text()).toBe(failureReason)
  expect(wrapper.find('repo').exists()).toBe(false)
  wrapper.unmount()
})
