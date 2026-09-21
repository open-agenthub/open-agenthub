import { describe, expect, it } from 'vitest'
import { parseNetworkPermission, permissionTitle } from './permissions.js'

describe('parseNetworkPermission', () => {
  it('parses egress port keys', () => {
    expect(parseNetworkPermission('NetworkPort(egress 5432/TCP)')).toEqual({
      direction: 'egress', port: 5432, protocol: 'TCP'
    })
  })

  it('parses browser_to_agent port keys with UDP', () => {
    expect(parseNetworkPermission('NetworkPort(browser_to_agent 3000/UDP)')).toEqual({
      direction: 'browser_to_agent', port: 3000, protocol: 'UDP'
    })
  })

  it('returns null for ordinary tools and malformed keys', () => {
    expect(parseNetworkPermission('Bash')).toBeNull()
    expect(parseNetworkPermission('NetworkPort(sideways 80/TCP)')).toBeNull()
    expect(parseNetworkPermission('NetworkPort(egress abc/TCP)')).toBeNull()
    expect(parseNetworkPermission(null)).toBeNull()
    expect(parseNetworkPermission(undefined)).toBeNull()
  })
})

describe('permissionTitle', () => {
  it('keeps the classic sentence for tool requests', () => {
    expect(permissionTitle('Bash')).toBe('The agent wants to use Bash.')
  })

  it('renders network port requests as a human sentence', () => {
    expect(permissionTitle('NetworkPort(egress 5432/TCP)'))
      .toBe('The agent asks to open network port 5432 (outgoing traffic, TCP).')
    expect(permissionTitle('NetworkPort(browser_to_agent 3000/TCP)'))
      .toBe('The agent asks to open network port 3000 (browser → agent, TCP).')
  })
})
