// Presentation helpers for pending permission requests. Network port requests ride on
// the same permission channel as tool requests, encoded in the tool name — render them
// as a human sentence instead of the raw key.
const NETWORK_TOOL = /^NetworkPort\((egress|browser_to_agent) (\d{1,5})\/(TCP|UDP)\)$/

const DIRECTION_LABELS = {
  egress: 'outgoing traffic',
  browser_to_agent: 'browser → agent'
}

/** Parses a network-port tool key, or returns null for ordinary tool requests. */
export function parseNetworkPermission(tool) {
  const match = NETWORK_TOOL.exec(tool || '')
  if (!match) return null
  return { direction: match[1], port: Number(match[2]), protocol: match[3] }
}

/** Headline for a pending permission request (network-aware). */
export function permissionTitle(tool) {
  const network = parseNetworkPermission(tool)
  if (!network) return `The agent wants to use ${tool}.`
  return `The agent asks to open network port ${network.port} (${DIRECTION_LABELS[network.direction]}, ${network.protocol}).`
}
