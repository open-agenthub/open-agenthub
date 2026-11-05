// @vitest-environment happy-dom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { enableAutoUnmount, mount } from '@vue/test-utils'
import HomeView from './HomeView.vue'
import SessionList from './SessionList.vue'
import SessionsView from './SessionsView.vue'
import { groupSessions, nestShared } from '../lib/projects.js'
import { needsYou } from '../lib/status.js'
import { detectAlerts } from '../lib/desktop-notify.js'

enableAutoUnmount(afterEach)
vi.mock('../api.js', () => ({ api: {}, config: { gitEnabled: false } }))

// #407/#410: what /api/sessions/shared returns for a parent shared with the viewer and the child
// its agent started — the child inherits the parent's role and names it as its parent.
const parent = { id: 'p', title: 'Release prep', phase: 'Running', mode: 'Interactive', accessRole: 'Collaborator', sharedBy: 'alice' }
const child = { id: 'c', title: 'Write changelog', phase: 'Running', mode: 'Autonomous', accessRole: 'Collaborator', sharedBy: 'alice', parentSessionId: 'p', inheritedFrom: 'p' }
const grandchild = { id: 'g', title: 'Check links', phase: 'Running', mode: 'Autonomous', accessRole: 'Collaborator', sharedBy: 'alice', parentSessionId: 'c', inheritedFrom: 'p' }
const own = { id: 'o', title: 'Mine', phase: 'Running', mode: 'Interactive', accessRole: 'Owner' }

describe('shared sessions in the list', () => {
  it('nests children under their parent, whatever order the API returned', () => {
    const nested = nestShared([grandchild, child, parent])
    expect(nested.map(s => [s.id, s.nestDepth, s.parentTitle || null])).toEqual([
      ['p', 0, null], ['c', 1, 'Release prep'], ['g', 2, 'Write changelog']
    ])
  })

  it('keeps a child whose parent the search filtered out at the top level', () => {
    expect(nestShared([child]).map(s => [s.id, s.nestDepth])).toEqual([['c', 0]])
  })

  it('lists sessions on a corrupt cycle once each', () => {
    const a = { id: 'a', title: 'A', parentSessionId: 'b' }
    const b = { id: 'b', title: 'B', parentSessionId: 'a' }
    expect(nestShared([a, b]).map(s => s.id).sort()).toEqual(['a', 'b'])
  })

  it('puts the whole tree in the sidebar group "Shared with me"', () => {
    const groups = groupSessions([], [own, grandchild, child, parent], '')
    const shared = groups.find(g => g.id === 'shared')
    expect(shared.sessions.map(s => s.id)).toEqual(['p', 'c', 'g'])
    expect(groups.find(g => g.id === 'ungrouped').sessions.map(s => s.id)).toEqual(['o'])
  })

  it('marks role, owner and parent in the sidebar row, without owner actions', () => {
    const wrapper = mount(SessionList, { props: { sessions: nestShared([child, parent]) } })
    const row = wrapper.find('[data-session-row="c"]')
    expect(row.find('.role').text()).toBe('· Collaborator')
    expect(row.find('.owner').text()).toBe('· by alice')
    expect(row.find('[data-child-marker]').text()).toBe('· child of Release prep')
    expect(row.classes()).toContain('nested')
    expect(row.attributes('data-child-of')).toBe('p')
    expect(row.find('.acts').exists()).toBe(false)
    expect(wrapper.find('[data-session-row="p"]').classes()).not.toContain('nested')
  })

  it('nests and marks them on the Sessions page as well', () => {
    const wrapper = mount(SessionsView, { props: { sessions: [own, child, parent] } })
    const rows = wrapper.findAll('.row')
    const titles = rows.map(r => r.find('.row-title').text())
    expect(titles.slice(titles.indexOf('Release prep'), titles.indexOf('Release prep') + 2)).toEqual(['Release prep', 'Write changelog'])
    const childRow = rows.find(r => r.find('.row-title').text() === 'Write changelog')
    expect(childRow.classes()).toContain('nested')
    expect(childRow.find('[data-shared-role]').text()).toBe('· Collaborator · by alice')
    expect(childRow.find('[data-child-marker]').text()).toBe('· child of Release prep')
  })
})

describe('roles in counters and alerts', () => {
  const viewerAsking = { ...parent, id: 'v', accessRole: 'Viewer', questionOpen: true }
  const collaboratorAsking = { ...parent, id: 'k', questionPending: true }

  it('counts a question as waiting on you only when you can answer it', () => {
    expect(needsYou(viewerAsking)).toBe(false)
    expect(needsYou(collaboratorAsking)).toBe(true)
    const wrapper = mount(SessionsView, { props: { sessions: [viewerAsking, collaboratorAsking] } })
    expect(wrapper.find('.sub').text()).toBe('2 total · 2 running · 1 waiting on you')
  })

  it('still finds a viewer session under its status filter', async () => {
    const wrapper = mount(SessionsView, { props: { sessions: [viewerAsking, own] } })
    await wrapper.findAll('.chip.filter').find(c => c.text() === 'Question open').trigger('click')
    expect(wrapper.findAll('.row-title').map(t => t.text())).toEqual(['Release prep'])
  })

  it('alerts a viewer when the session ends, not when it asks', () => {
    const before = { ...viewerAsking, questionOpen: false }
    expect(detectAlerts([before], [viewerAsking])).toEqual([])
    expect(detectAlerts([before], [{ ...before, phase: 'Succeeded' }])).toEqual([{ id: 'v', title: 'Release prep', kind: 'finished' }])
    expect(detectAlerts([{ ...collaboratorAsking, questionPending: false }], [collaboratorAsking])).toHaveLength(1)
  })
})


describe('shared sessions on home', () => {
  it('keeps direct, inherited and project shares visible beyond the owned preview', async () => {
    const project = { ...parent, id: 'project', title: 'Project task', accessRole: 'Viewer', inheritedFromProjectId: 'project-id', questionOpen: true }
    const wrapper = mount(HomeView, { props: { sessions: [
      ...Array.from({ length: 8 }, (_, i) => ({ ...own, id: `own-${i}` })),
      { ...parent, questionPending: true }, child, project
    ] } })
    const rows = wrapper.findAll('[data-home-shared] .row')
    expect(rows.map(r => r.attributes('data-home-session'))).toEqual(['p', 'c', 'project'])
    expect(rows[0].text()).toContain('Collaborator · shared by alice')
    expect(rows[2].text()).toContain('Question open')
    expect(rows[2].text()).toContain('Viewer · shared by alice')
    expect(wrapper.find('.headline').text()).toBe('One agent wants a word with you.')
    expect(wrapper.findAll('.ny-card')).toHaveLength(1)
    expect(wrapper.find('.ny-card').text()).toContain('shared by alice')
    await rows[2].trigger('click')
    expect(wrapper.emitted('select')).toEqual([['project']])
    await wrapper.setProps({ sessions: [own] })
    expect(wrapper.find('[data-home-shared]').exists()).toBe(false)
  })
})
