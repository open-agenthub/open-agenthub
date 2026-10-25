# Issue 251 local evidence

- Frontend suite: 855 tests passed.
- Backend transcript/context-clear selection: 85 tests passed.
- Frontend production build passed.
- Chromium, 20,000 synthetic entries through the real backend paginator: latest 500 rendered
  in 476 ms, one conversation request; Load earlier fetched exactly 500 more. The visible
  message anchor moved 0.625 CSS pixels after prepend.
- Main runtime/real pinned CLI/actual MCP and backend clear chain: scheduled-result to final
  old answer 4.031 seconds; handover entered fresh context without the old task marker. Three
  later turns were present in the actual live reader, Chromium and after reload. Combined native
  history reparsed successfully. Raw cursor advanced from 314059 to 317771 on the first later turn.
- rc.5 runtime comparison: scheduled-result to final old answer 4.026 seconds; the same three-turn
  live/Chromium/reload sequence passed using the current backend reader. Its cursor/identity
  algorithm matches the release reader; pod-request plumbing changed since that release.

The model, session store, pod identity and unrelated browser panel APIs are local fixtures.
No affected-cluster session or stored object-storage upload/download path was verified. The
reported context-clear freeze was not reproduced, and issue 251 remains open for that investigation.
