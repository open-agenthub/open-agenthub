import { describe, expect, it } from 'vitest'
import { draftAllowsAnything, draftFromScope, emptyScopeDraft, scopeFromDraft, scopeSummary } from './token-scope.js'

const accounts = {
  Claude: [{ id: 'work0001', label: 'Work' }, { id: 'home0002', label: 'Personal' }],
  Codex: [{ id: 'cx000001', label: 'Codex' }],
  Cursor: [],
  OpenClaw: []
}
const pats = [
  { id: 'a', kind: 'gitlab', host: 'gitlab.example.com' },
  { id: 'b', kind: 'github', host: 'github.com' },
  { id: 'c', kind: 'github', host: 'github.your-org.example' }
]

describe('token scope helpers', () => {
  it('turns a draft into an allow list that names only agents with a ticked account', () => {
    const draft = emptyScopeDraft()
    draft.providerAccounts.Claude = ['work0001']
    draft.gitPats = ['a', 'b']
    draft.apiKeys = true
    expect(scopeFromDraft(draft)).toEqual({
      providerAccounts: { Claude: ['work0001'] }, gitPats: ['a', 'b'], apiKeys: true
    })
    expect(scopeFromDraft(emptyScopeDraft())).toEqual({ providerAccounts: {}, gitPats: [], apiKeys: false })
  })

  it('prefills a draft from a stored restriction, expanding the wildcard to what is stored now', () => {
    const draft = draftFromScope({ providerAccounts: { claude: ['*'], Codex: ['cx000001', 'gone'] }, gitPats: ['b'] }, accounts, pats)
    expect(draft.providerAccounts.Claude).toEqual(['work0001', 'home0002'])
    expect(draft.providerAccounts.Codex).toEqual(['cx000001'])
    expect(draft.providerAccounts.Cursor).toEqual([])
    expect(draft.gitPats).toEqual(['b'])
    expect(draft.apiKeys).toBe(false)
    expect(draftFromScope(null, accounts, pats)).toEqual(emptyScopeDraft())
  })

  it('flags a draft that allows nothing', () => {
    expect(draftAllowsAnything(emptyScopeDraft())).toBe(false)
    expect(draftAllowsAnything({ ...emptyScopeDraft(), apiKeys: true })).toBe(true)
    expect(draftAllowsAnything({ ...emptyScopeDraft(), gitPats: ['a'] })).toBe(true)
  })

  it('summarises a restriction with labels and counts', () => {
    expect(scopeSummary(null, accounts, pats)).toBe('unrestricted')
    expect(scopeSummary({ providerAccounts: { Claude: ['work0001'] }, gitPats: ['a', 'b'] }, accounts, pats))
      .toBe('Claude: Work · git: 2 of 3')
    expect(scopeSummary({ providerAccounts: { codex: ['*'] }, gitPats: ['*'], apiKeys: true }, accounts, pats))
      .toBe('Codex: any · git: any · API keys')
    // An account removed since the restriction was written is counted by id, not dropped silently.
    expect(scopeSummary({ providerAccounts: { Claude: ['gone0000'] } }, accounts, pats)).toBe('Claude: gone0000')
    expect(scopeSummary({ providerAccounts: {}, gitPats: [] }, accounts, pats)).toBe('nothing allowed')
  })
})
