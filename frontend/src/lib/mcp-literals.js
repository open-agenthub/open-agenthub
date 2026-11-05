// Inspect only the fields passed to the MCP client; warnings never echo their values.
export function hasLiteralMcpValues(configJson) {
  let config
  try { config = JSON.parse(configJson) } catch { return false }
  if (!config || typeof config !== 'object' || Array.isArray(config)) return false
  return ['headers', 'env'].some(field => {
    const values = config[field]
    if (!values || typeof values !== 'object' || Array.isArray(values)) return false
    return Object.values(values).some(value => {
      if (typeof value !== 'string') return value != null
      const text = value.trim()
      if (!text) return false
      // Only a whole variable reference (optionally an auth scheme) avoids the warning.
      // A literal fallback or a mixed token still distributes a value to recipients.
      return !/^(?:[A-Za-z]+\s+)?\$\{[A-Za-z_][A-Za-z0-9_]*\}$/.test(text)
    })
  })
}
