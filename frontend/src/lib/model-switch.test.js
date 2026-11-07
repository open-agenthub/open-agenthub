import { describe, it, expect } from 'vitest'
import { busyReason, currentModel, switchErrorText, switchModeNote, validModelId } from './model-switch.js'

describe('model switch wording', () => {
  it('says before the click whether a pick switches now or at the next restart', () => {
    expect(switchModeNote('live', 'Claude')).toMatch(/running conversation.*keeps its context/)
    expect(switchModeNote('restart', 'Cursor')).toMatch(/next restart.*Cursor/)
    expect(switchModeNote('restart', 'Codex')).toMatch(/own model ids/)
    expect(switchModeNote('unsupported')).toMatch(/cannot be changed/)
  })

  it('reads a running turn, an open question and an approval as reasons to wait', () => {
    expect(busyReason({ state: 'working' })).toBe('turn_running')
    expect(busyReason({ state: 'waiting', reason: 'answer' })).toBe('question_open')
    expect(busyReason({ state: 'waiting', reason: 'approval' })).toBe('approval_open')
    expect(busyReason({ state: 'idle' }, { pendingPermissions: 1 })).toBe('approval_open')
    expect(busyReason({ state: 'idle' }, { menuOpen: true })).toBe('question_open')
    expect(busyReason({ state: 'idle' })).toBe('')
    expect(busyReason({ state: 'unknown' })).toBe('')
  })

  it('turns every refusal into a sentence and quotes the agent where it spoke', () => {
    expect(switchErrorText('turn_running')).toMatch(/working/)
    expect(switchErrorText('model_refused', "Model 'x' is unavailable")).toBe("The agent refused this model. It said: “Model 'x' is unavailable”")
    expect(switchErrorText('model_switch_wrong_model', 'mock/mock-a')).toMatch(/switched to “mock\/mock-a” instead/)
    expect(switchErrorText('something_new')).toBe('The switch failed.')
  })

  it('shows a confirmed switch over the stored model, and the stored one over the CLI default', () => {
    expect(currentModel({ selected: 'a' }, 'b')).toBe('b')
    expect(currentModel({ selected: 'a' }, '')).toBe('a')
    expect(currentModel({ selected: null }, '')).toBe('')
  })

  it('takes ids as the hub does', () => {
    for (const id of ['gpt-6-luna', 'claude-fable-5-1[1m]', 'opencode-go/glm-5.3']) expect(validModelId(id)).toBe(true)
    for (const id of ['', 'two words', '-c', 'a\u0007b', 'x'.repeat(201)]) expect(validModelId(id)).toBe(false)
  })
})
