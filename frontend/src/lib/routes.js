export function sharedTokenFromPath(pathname) {
  const match = String(pathname || '').match(/^\/shared\/([^/]+)\/?$/)
  return match ? decodeURIComponent(match[1]) : null
}
