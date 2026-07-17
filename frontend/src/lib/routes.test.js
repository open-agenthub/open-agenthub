import { describe, expect, it } from 'vitest'
import { sharedTokenFromPath } from './routes.js'

describe('sharedTokenFromPath', () => {
  it('extracts and decodes a secret-link token', () => {
    expect(sharedTokenFromPath('/shared/token%2Fvalue')).toBe('token/value')
  })

  it('ignores normal application routes', () => {
    expect(sharedTokenFromPath('/s/session-1')).toBeNull()
  })
})
