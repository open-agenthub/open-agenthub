# Dependency refresh proof

Run from the repository root after installing frontend, personal MCP and session-agent npm
dependencies. The scripts check the installed graph and real protocol/browser behavior rather
than inferring compatibility from manifest edits. Keep generated reports and screenshots out
of the repository; record results and limitations in the pull request.

- `node tests/DependencyAlertProof/check-alerts.cjs` compares lockfile resolutions with live
  advisory ranges. Set `ALERT_GIT_REF` to an unchanged baseline or the fetched PR merge ref.
  A vulnerable baseline intentionally exits 1. This repository-specific helper handles npm
  lockfiles and direct backend package references, not arbitrary dependency ecosystems.
- `node tests/DependencyAlertProof/stdio-proof.mjs` starts the shipped personal and in-pod MCP
  servers over pipes, then checks initialize/listTools/session_list through the installed SDKs.
  Its HTTP stand-in verifies expected auth headers; it does not prove deployed authorization.
- Build frontend and start `PORT=5173 node tests/DependencyAlertProof/browser-fixture.cjs`, then
  run browser-proof.cjs with AGENTHUB_CDP_URL and AGENTHUB_UI_URL set. It checks real production
  conversation diagram rendering, dimensions and browser errors. AGENTHUB_SCREENSHOT optionally
  saves a screenshot outside the repository. API data and tokens are synthetic.
- `node tests/DependencyAlertProof/bundle-sizes.cjs <baseline dist> <proposed dist>` compares
  entry and all-JavaScript raw/gzip sizes. Build both from the same source to isolate the graph.

For #276, released dependency ranges already accept the patched transitive middleware, so an
override was unnecessary and rejected. Direct validator versions remain pinned; the frontend
test dependency follows its existing caret convention. Actual alert closure is checked after
coordinator merge and default-branch dependency scanning, not inferred from a clean branch.
