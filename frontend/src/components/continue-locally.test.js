// @vitest-environment happy-dom
import { mount, flushPromises } from '@vue/test-utils'
import { describe, it, expect, vi, beforeEach } from 'vitest'

vi.mock('../api.js', () => ({ api: { takeSessionLocal: vi.fn(), sessionHandBackLink: vi.fn() } }))
import { api } from '../api.js'
import ContinueLocallyDialog from './ContinueLocallyDialog.vue'

const link = (name) => ({
  url: 'https://hub.example.com/api/session-transfer', secret: 'oat_x', expiresAt: '2026-11-05T12:15:00Z',
  command: `${name} posix`, powerShellCommand: `${name} powershell`
})
const open = session => mount(ContinueLocallyDialog, { props: { session: { id: 's1', agent: 'Claude', ...session } } })

beforeEach(() => {
  localStorage.clear()
  vi.mocked(api.takeSessionLocal).mockReset()
  vi.mocked(api.sessionHandBackLink).mockReset()
})

describe('continue locally', () => {
  it('says that a live session is paused and creates the command only for an absolute directory', async () => {
    vi.mocked(api.takeSessionLocal).mockResolvedValue({ paused: true, resumeId: 'r-1', link: link('take') })
    const wrapper = open({ phase: 'Running' })
    expect(wrapper.find('[data-pause-warning]').exists()).toBe(true)
    expect(wrapper.get('[data-take-local]').text()).toBe('Pause and create command')

    await wrapper.get('[data-local-directory]').setValue('project')
    expect(wrapper.get('[data-take-local]').attributes('disabled')).toBeDefined()

    await wrapper.get('[data-local-directory]').setValue('/home/me/project')
    await wrapper.get('[data-take-local]').trigger('click'); await flushPromises()

    expect(api.takeSessionLocal).toHaveBeenCalledWith('s1', { directory: '/home/me/project', confirm: true })
    expect(wrapper.get('[data-take-command]').text()).toBe('take posix')
    expect(wrapper.text()).toContain('claude --resume r-1')
    expect(wrapper.emitted('changed')).toBeTruthy()
  })

  it('switches the shown command to PowerShell', async () => {
    vi.mocked(api.takeSessionLocal).mockResolvedValue({ paused: false, resumeId: 'r-1', link: link('take') })
    const wrapper = open({ phase: 'Paused' })
    await wrapper.get('[data-local-directory]').setValue('C:\\Users\\me\\project')
    await wrapper.get('[data-take-local]').trigger('click'); await flushPromises()
    await wrapper.get('input[value="powershell"]').setValue()
    expect(wrapper.get('[data-take-command]').text()).toBe('take powershell')
    expect(wrapper.emitted('changed')).toBeFalsy()
  })

  it('remembers the directory per session for the hand-back, and refuses it while the session runs', async () => {
    vi.mocked(api.sessionHandBackLink).mockResolvedValue({ resumeId: 'r-1', link: link('back') })
    const first = open({ phase: 'Paused' })
    await first.get('[data-local-directory]').setValue('/home/me/project')

    const live = open({ phase: 'Running' })
    expect(live.get('[data-local-directory]').element.value).toBe('/home/me/project')
    expect(live.get('[data-hand-back]').attributes('disabled')).toBeDefined()

    const paused = open({ phase: 'Paused' })
    await paused.get('[data-hand-back-resume]').setValue(false)
    await paused.get('[data-hand-back]').trigger('click'); await flushPromises()
    expect(api.sessionHandBackLink).toHaveBeenCalledWith('s1', { directory: '/home/me/project', resume: false })
    expect(paused.get('[data-hand-back-command]').text()).toBe('back posix')
  })

  it('shows the refusal the hub gives', async () => {
    vi.mocked(api.takeSessionLocal).mockRejectedValue(new Error('The session has no stored conversation yet.'))
    const wrapper = open({ phase: 'Paused' })
    await wrapper.get('[data-local-directory]').setValue('/home/me/project')
    await wrapper.get('[data-take-local]').trigger('click'); await flushPromises()
    expect(wrapper.get('[data-transfer-error]').text()).toContain('no stored conversation')
  })
})
