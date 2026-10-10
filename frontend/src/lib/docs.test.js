import { describe, expect, it } from 'vitest'
import { DEV_VERSION, REPO_URL, docsUrl, versionLabel } from './docs.js'

describe('versionLabel', () => {
  it('prefixes a release number with v', () => {
    expect(versionLabel('0.12.0')).toBe('v0.12.0')
    expect(versionLabel('1.0.0-rc.1')).toBe('v1.0.0-rc.1')
  })

  it('keeps a commit hash bare, since "va1b2c3d" is not a version', () => {
    expect(versionLabel('a1b2c3d')).toBe('a1b2c3d')
  })

  it('falls back to dev for anything a build did not stamp', () => {
    for (const v of [undefined, null, '', '   ', DEV_VERSION]) expect(versionLabel(v)).toBe(DEV_VERSION)
  })
})

describe('project links', () => {
  it('points at the public repository', () => {
    expect(REPO_URL).toBe('https://github.com/open-agenthub/open-agenthub')
  })

  it('builds the documentation root without a trailing slash', () => {
    expect(docsUrl('')).toBe('https://open-agenthub.github.io/docs')
  })
})
