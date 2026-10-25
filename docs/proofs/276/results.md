# Dependency alert proof (#276)

The default-branch lockfiles reproduced all five open high advisory resolutions: Zod 4.6.5
in both MCP graphs, body-parser 2.3.0 in both, and happy-dom 20.14.6 in frontend tests.
The updated lockfiles resolve Zod 4.6.7, body-parser 2.4.0 and happy-dom 21.0.4. The live range
check finds zero vulnerable resolutions and all three npm audits report zero vulnerabilities.

No overrides were added: the installed SDK and its HTTP middleware parent already accept the
patched body-parser release within their declared ranges. Raising an override was rejected
because it would replace a constraint that already works. Zod stays an exact direct pin; the
frontend test dependency keeps the surrounding caret convention. The SDK version is unchanged.

Zod participates in MCP input validation. body-parser is transitive HTTP middleware in the SDK
graph; these shipped MCP entrypoints run over stdio, and this refresh does not add an HTTP
listener. happy-dom is a development/test dependency, absent from the production browser bundle.
Affected versions were removed even where exploit reachability depends on a different transport.

## Execution proof

- The real personal and in-pod MCP servers initialized over stdio, listed their tools, and called
  session_list through their installed SDKs. A local HTTP stand-in recorded the expected Bearer
  versus callback-token headers. This proves transport/schema compatibility, not deployed auth.
- Frontend: 879 tests passed. Personal MCP: 51 passed. Session runtime: 754 passed using the
  conventional 0022 umask. A first runtime run under a restrictive inherited umask failed the
  archive fixture's mode-change assertion; no production/archive source was changed.
- Backend: 2,319 passed with real Postgres, zero skipped; transitive package audit clean.
- Production frontend built; entry and all-JavaScript raw/gzip sizes exactly match the same
  source built with the original lockfile (zero-byte delta). The changed frontend dependency is
  test-only. Real Chromium rendered flowchart and sequence diagrams in the production conversation
  view with nonzero dimensions and no page/console errors; screenshot and JSON are alongside.
- Runtime dependencies were installed with scripts disabled in this non-root pod; the unchanged
  node-pty 1.1.0 binary from the running image was reused locally. Image CI rebuilds it normally.

## Reproduce

Install the three npm package graphs, then run:

```sh
node docs/proofs/276/check-alerts.cjs
node docs/proofs/276/stdio-proof.mjs
ALERT_GIT_REF=<baseline commit> node docs/proofs/276/check-alerts.cjs
```

The baseline check intentionally exits 1; current and fetched PR merge-preview graphs must
exit 0. After a push, fetch `refs/pull/<PR>/merge` and check `ALERT_GIT_REF=FETCH_HEAD` too.
The check compares actual lockfile resolutions against the live advisory API, rather than
assuming the package manager has ingested every advisory. Actual alert closure is checked only
after the coordinator merges and default-branch dependency scanning completes.

For browser proof, build frontend, start `PORT=5173 node docs/proofs/276/browser-fixture.cjs`,
then set AGENTHUB_CDP_URL, AGENTHUB_UI_URL and AGENTHUB_SCREENSHOT when running browser-proof.cjs.
The API data is synthetic and no live model session is claimed. bundle-sizes.cjs takes baseline
and updated dist directories. None of the fixture tokens are real user credentials.
