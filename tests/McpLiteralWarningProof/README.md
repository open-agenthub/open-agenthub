# MCP literal warning Chromium proof

Runs the real Vue pane and styles in Chromium with isolated library API fixtures. This
proves editor and sharing behavior and payload preservation, not provider expansion or
pod delivery. All values are synthetic. Screenshots belong in Files, never in Git.

From the repository root, install frontend and browser-runtime dependencies with
`npm ci --ignore-scripts` in each directory, then run:

```sh
frontend/node_modules/.bin/vite --config tests/McpLiteralWarningProof/vite.config.mjs
CDP_URL=http://example.com:9222 UI_URL=http://example.com:5173 PROOF_OUTPUT=/tmp/mcp-warning node tests/McpLiteralWarningProof/browser.cjs
```

Use reachable test-host addresses for the environment variables. The harness checks both
personal and org modes at 1440 and 390 px, saves screenshots of both warnings, confirms
no horizontal overflow or page errors, and exercises saving and sharing without blocking.
The frontend suite covers incomplete JSON, references, mixed literals, API entries and
live editor updates. Existing backend suites exercise access resolution and assembly;
backend delivery code is unchanged.
