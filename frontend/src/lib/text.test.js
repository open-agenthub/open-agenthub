import { describe, it, expect } from 'vitest'
import { repoShortName, sessionMatches } from './text.js'

describe('repoShortName', () => {
  it('strips https host and .git', () => {
    expect(repoShortName('https://gitlab.example.com/team/app.git')).toBe('team/app')
  })
  it('strips ssh prefix', () => {
    expect(repoShortName('git@github.com:org/repo.git')).toBe('org/repo')
  })
  it('handles empty', () => {
    expect(repoShortName('')).toBe('')
  })
})

describe('sessionMatches', () => {
  const s = { title: 'Fix billing', repoUrl: 'https://x/team/pay.git', mode: 'Autonomous', repos: [{ url: 'https://x/team/pay.git' }] }
  it('matches empty query', () => expect(sessionMatches(s, '')).toBe(true))
  it('matches by title', () => expect(sessionMatches(s, 'billing')).toBe(true))
  it('matches by repo', () => expect(sessionMatches(s, 'pay')).toBe(true))
  it('matches by mode', () => expect(sessionMatches(s, 'autonomous')).toBe(true))
  it('no match', () => expect(sessionMatches(s, 'zzz')).toBe(false))
})
