// @vitest-environment happy-dom
import { mount, flushPromises } from '@vue/test-utils'
import { describe, it, expect, vi, beforeEach } from 'vitest'

vi.mock('../api.js', () => ({ api: { moveSessionProject: vi.fn(), pauseSession: vi.fn() } }))
import { api } from '../api.js'
import MoveSessionDialog from './MoveSessionDialog.vue'

const projects = [{ id: 'source', name: 'Source', secretIds: ['old'] }, { id: 'target', name: 'Target', secretIds: ['new'] }]
const open = session => mount(MoveSessionDialog, { attachTo: document.body,
  props: { session: { id: 's', title: 'Nightly', projectId: 'source', ...session }, projects, sessions: [], target: 'target' } })
const confirm = wrapper => wrapper.get('[data-confirm-move]')

beforeEach(() => { vi.mocked(api.moveSessionProject).mockReset(); vi.mocked(api.pauseSession).mockReset() })

describe('moving a scheduled session', () => {
  it('moves an idle schedule without pausing and without stopping a run', async () => {
    vi.mocked(api.moveSessionProject).mockResolvedValue({})
    const wrapper = open({ mode: 'Scheduled', phase: 'Scheduled' })
    expect(wrapper.get('[data-project-move-impact]').text()).toContain('next run starts with the destination project’s secrets only')
    expect(confirm(wrapper).text()).toBe('Confirm move')
    await confirm(wrapper).trigger('click'); await flushPromises()
    expect(api.moveSessionProject).toHaveBeenCalledWith('s', 'target', { stopActiveRun: false })
    expect(api.pauseSession).not.toHaveBeenCalled()
    expect(wrapper.emitted('close')).toBeTruthy()
  })

  it('stops a run in progress only after the owner chooses Stop run and move', async () => {
    vi.mocked(api.moveSessionProject).mockResolvedValue({})
    const wrapper = open({ mode: 'Scheduled', phase: 'Running' })
    expect(wrapper.find('[data-scheduled-run-active]').exists()).toBe(true)
    expect(confirm(wrapper).text()).toBe('Stop run and move')
    await confirm(wrapper).trigger('click'); await flushPromises()
    expect(api.moveSessionProject).toHaveBeenCalledWith('s', 'target', { stopActiveRun: true })
    expect(api.pauseSession).not.toHaveBeenCalled()
  })

  it('offers to finish an interrupted move into the project the session already names', async () => {
    vi.mocked(api.moveSessionProject).mockResolvedValue({})
    const wrapper = mount(MoveSessionDialog, { attachTo: document.body, props: { projects, sessions: [],
      session: { id: 's', title: 'Nightly', projectId: 'target', mode: 'Scheduled', phase: 'Scheduled', scheduleMoveUnfinished: true } } })
    expect(wrapper.get('[data-schedule-move-unfinished]').text()).toContain('schedule is paused')
    expect(confirm(wrapper).text()).toBe('Finish move')
    expect(confirm(wrapper).attributes('disabled')).toBeUndefined()
    await confirm(wrapper).trigger('click'); await flushPromises()
    expect(api.moveSessionProject).toHaveBeenCalledWith('s', 'target', { stopActiveRun: false })
  })

  it('turns a run that started after the list was read into an explicit choice', async () => {
    vi.mocked(api.moveSessionProject)
      .mockRejectedValueOnce(Object.assign(new Error('A scheduled run is in progress.'), { code: 'scheduled_run_active' }))
      .mockResolvedValueOnce({})
    const wrapper = open({ mode: 'Scheduled', phase: 'Scheduled' })
    await confirm(wrapper).trigger('click'); await flushPromises()
    expect(wrapper.get('[role="alert"]').text()).toContain('A scheduled run is in progress.')
    expect(wrapper.emitted('close')).toBeFalsy()
    expect(confirm(wrapper).text()).toBe('Stop run and move')
    await confirm(wrapper).trigger('click'); await flushPromises()
    expect(vi.mocked(api.moveSessionProject).mock.calls.map(c => c[2])).toEqual([{ stopActiveRun: false }, { stopActiveRun: true }])
  })
})
