import { describe, expect, it } from 'vitest'
import { sessionCapabilities } from './access.js'

describe('sessionCapabilities', () => {
  it('makes viewers read-only', () => {
    expect(sessionCapabilities({ accessRole: 'Viewer' })).toEqual({
      canWrite: false,
      canManage: false,
      canShell: false
    })
  })

  it('lets collaborators write without owner controls', () => {
    expect(sessionCapabilities({ accessRole: 'Collaborator' })).toEqual({
      canWrite: true,
      canManage: false,
      canShell: false
    })
  })

  it('treats an absent role as owner access', () => {
    expect(sessionCapabilities({})).toEqual({
      canWrite: true,
      canManage: true,
      canShell: true
    })
  })
})
