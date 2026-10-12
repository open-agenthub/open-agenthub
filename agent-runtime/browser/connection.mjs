// A second connection can also enable emulated focus for every tab, hiding the viewer's
// active page from the supervisor's clipboard targeting. Preserve native focus here too.
export function connectBrowser(engine, endpoint) {
  return engine.connectOverCDP(endpoint, { timeout: 30_000, noDefaults: true });
}
