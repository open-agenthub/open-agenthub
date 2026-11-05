import { describe, expect, it } from 'vitest'
import { hasLiteralMcpValues } from './mcp-literals.js'

describe('raw MCP literal values', () => {
  it.each([
    { headers: { Authorization: 'Bearer demo' } },
    { env: { TOKEN: 'demo' } },
    { env: { PORT: 3000 } },
    { env: { TOKEN: '${TOKEN:-demo}' } },
    { headers: { Authorization: '${TOKEN}suffix' } },
    { headers: { Accept: 'application/json' } }
  ])('detects literal values without guessing credential names: %j', config => {
    expect(hasLiteralMcpValues(JSON.stringify(config))).toBe(true)
  })
  it.each([
    {}, { headers: { Authorization: 'Bearer ${TOKEN}' } },
    { env: { TOKEN: '${TOKEN}', EMPTY: '' } }, { headers: null },
    { url: 'https://mcp.example.com', args: ['demo'] }, [], null
  ])('ignores references and unrelated fields: %j', config => {
    expect(hasLiteralMcpValues(JSON.stringify(config))).toBe(false)
  })
  it('tolerates incomplete JSON while typing', () => {
    expect(hasLiteralMcpValues('{')).toBe(false)
  })
})
