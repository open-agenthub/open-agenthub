# Dependency security refresh (#225)

The SDK parent is updated to 1.32.1 and transitive dependencies refreshed within
supported ranges. Mermaid 12.2.0 permits patched KaTeX; the latest 11.x parent
remains on KaTeX 0.16. We prefer the released parent over an incompatible override.
The XML signature package retains a direct patched reference because its parent's
transitive floor still allows vulnerable versions. No npm overrides are needed.
Leaving the old lockfiles in place was rejected: compatible ranges alone do not
change the versions installed by `npm ci`.

## Reachability

The personal and in-pod MCP servers use stdio. Their SDK brings HTTP server
packages (proxy-addr, qs, hono and ip-address through the HTTP rate limiter), but
these entrypoints do not expose SDK HTTP listeners. Schema validation through the
SDK makes the fast-uri dependency relevant to its schema-processing surface.
DOMPurify and KaTeX are part of the lazy diagram renderer, which processes agent
text in markdown views; that is an untrusted-content boundary. The XML package
is resolved through the authentication/data-protection stack. This review does
not claim that every advisory has a demonstrated exploit in our configuration:
all affected resolutions are removed regardless of conditional reachability.

## Verification on merge preview a82bda4 + the dependency changes

- Session-agent: 747 passed, zero failed/skipped.
- Personal MCP: 51 passed, zero failed/skipped.
- Frontend: 854 passed across 95 files; production build passed.
- Backend against rootless Postgres 15: 2,281 passed, zero failed/skipped.
- Backend build: zero warnings/errors. Transitive NuGet vulnerability listing: none.
- All three npm audits: zero vulnerabilities.
- Real personal and in-pod MCP processes: initialize, listTools and session_list
  succeed over SDK stdio transports and authenticated HTTP to a callback fixture.
- Production frontend in real Chromium: flowchart and sequence SVGs render in the
  actual conversation markdown view; no console errors or uncaught page errors.
  The browser uses synthetic conversation data, not a live model or deployment.
- Live API reports 53 open alerts. The checker reproduces 53 vulnerable resolutions
  on origin/main and finds zero on this proposed merge graph. Actual alert closure
  is evaluated on the default branch after coordinator merge, not on this draft.

## Reproduce

Install dependencies in `frontend`, `mcp/agenthub` and `agent-runtime/session-agent`
(use `npm ci --ignore-scripts` for session-agent when no native build toolchain is
available). Run their `npm test` commands and `npm run build` in frontend.
Run the backend suite with `AGENTHUB_TEST_POSTGRES_CONNECTION_STRING` pointing to
an isolated Postgres instance, then build and run `dotnet list ... package
--vulnerable --include-transitive` for `backend/AgentHub.Api.csproj`.

From the repository root:

```sh
node docs/proofs/225/check-alerts.cjs
ALERT_GIT_REF=origin/main node docs/proofs/225/check-alerts.cjs
node docs/proofs/225/stdio-proof.mjs
node docs/proofs/225/browser-fixture.cjs
# In another shell, provide your Chromium CDP endpoint and the fixture's reachable origin:
AGENTHUB_CDP_URL=http://example.com:9222 AGENTHUB_UI_URL=http://example.com:5173 node docs/proofs/225/browser-proof.cjs
```

The before-alert command intentionally exits 1 while the base still has alerts.
Use an isolated port for the browser fixture; `PORT` defaults to 5173. Build an
unmodified main snapshot separately, then compare with:

```sh
node docs/proofs/225/bundle-sizes.cjs /path/to/main/frontend/dist frontend/dist
```

The initial entry grows 1,337 bytes (464 gzip); all JavaScript chunks combined grow
1,722,455 bytes (519,723 gzip). Diagram loading remains lazy. See `bundle-sizes.json`
for absolute sizes; these measure the complete frontend dependency refresh rather
than attributing every byte solely to Mermaid.

PR #91's SDK/browser-client upgrades and #71's diagram-parent upgrade are covered.
PR #72 changes the Node 26 base image, an independent runtime compatibility
choice; it stays open and is not superseded by package security updates.
